using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Host;

/// <summary>Streams one CfSharp local journal file to an Adapter Worker.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "The owning Supervisor disposes the per-instance operation gate after the pipe closes.")]
public sealed class AdapterWorkerUploadClient
{
    private const int MaximumChunkBytes = 1024 * 1024;
    private readonly AdapterWorkerReadRangeClient _channel;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _pendingLock = new();
    private TaskCompletionSource<string>? _completion;
    private TaskCompletionSource<Guid>? _ready;
    private Guid _requestId;
    private Guid _streamId;
    private Guid _operationId;
    private string? _rootKey;
    private bool _closed;

    public AdapterWorkerUploadClient(
        AdapterWorkerReadRangeClient channel,
        InstanceId instanceId,
        WorkerSessionId sessionId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _instanceId = instanceId;
        _sessionId = sessionId;
    }

    public async ValueTask<string> UploadAsync(
        MirrorPulseWorkerUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        if (request.InstanceId != _instanceId || request.Length < 0 ||
            !request.Content.CanRead || request.Content.Length - request.Content.Position < request.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The Worker upload is invalid.");
        }

        Guid operationId = request.OperationId ?? Guid.NewGuid();
        if (operationId == Guid.Empty) throw new ArgumentException("The Worker operation ID must not be empty.", nameof(request));
        Guid requestId = _channel.ProtocolVersion == 2 ? Guid.NewGuid() : operationId;
        if (requestId == Guid.Empty) throw new ArgumentException("The Worker operation ID must not be empty.", nameof(request));
        string? expectedHash = request.ExpectedContentSha256 is { } expected
            ? Sha256Digest.Parse(expected).Hexadecimal : null;
        using IncrementalHash? contentHash = expectedHash is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void VerifyTransmittedContent()
        {
            if (contentHash is not null && Convert.ToHexString(contentHash.GetHashAndReset()) != expectedHash)
                throw new InvalidDataException("The transmitted content no longer matches the durable upload intent.");
        }
        Guid streamId = _channel.ProtocolVersion == 2 ? Guid.NewGuid() : request.OperationId ?? Guid.NewGuid();
        JsonElement payload = _channel.MutationPayload(request.RootKey, new
        {
            path = request.NormalizedPath,
            expectedRevision = request.ExpectedRevision,
            length = request.Length,
            streamId,
            operationId = request.OperationId,
        }, operationId, expectedRevision: request.ExpectedRevision,
            destinationMustBeAbsent: request.ExpectedRevision is null && request.DestinationMustBeAbsent);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ready = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _requestId = requestId;
                _operationId = operationId;
                _rootKey = request.RootKey;
                _ready = ready;
                _completion = completion;
            }

            await _channel.WriteControlAsync(new ControlFrameEnvelope(1, "Upload", requestId,
                _instanceId, _sessionId, false, payload), cancellationToken).ConfigureAwait(false);
            Guid acceptedStream = await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (acceptedStream != streamId)
            {
                throw new InvalidDataException("The Worker accepted a different upload stream.");
            }

            long offset = 0;
            while (offset < request.Length)
            {
                int count = checked((int)Math.Min(MaximumChunkBytes, request.Length - offset));
                byte[] buffer = new byte[count];
                await request.Content.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                contentHash?.AppendData(buffer);
                // A surviving writable mapping can alter a normal shared read handle.
                // Bind the durable proof to the bytes actually sent, before the final
                // frame authorizes the Worker to commit its temporary upload.
                if (offset + count == request.Length) VerifyTransmittedContent();
                await _channel.WriteChunkAsync(new BinaryChunkFrame(requestId, _instanceId, _sessionId,
                    streamId, offset, buffer, offset + count == request.Length,
                    Sha256Digest.Parse(Convert.ToHexString(SHA256.HashData(buffer))))
                { RootKey = request.RootKey }, cancellationToken)
                    .ConfigureAwait(false);
                offset += count;
            }

            if (request.Length == 0)
            {
                VerifyTransmittedContent();
                await _channel.WriteChunkAsync(new BinaryChunkFrame(requestId, _instanceId, _sessionId,
                    streamId, 0, ReadOnlyMemory<byte>.Empty, true,
                    Sha256Digest.Parse(Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty))))
                { RootKey = request.RootKey }, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_completion, completion))
                {
                    _completion = null;
                    _ready = null;
                    if (!completion.Task.IsCompleted)
                    {
                        _closed = true;
                    }
                }
            }

            _operation.Release();
        }
    }

    public bool CanHandle(ControlFrameEnvelope frame)
    {
        lock (_pendingLock)
        {
            return !_closed && _completion is not null && frame.RequestId == _requestId &&
                frame.InstanceId == _instanceId && frame.WorkerSessionId == _sessionId;
        }
    }

    public ValueTask HandleResponseAsync(ControlFrameEnvelope frame)
    {
        TaskCompletionSource<Guid>? ready;
        TaskCompletionSource<string>? completion;
        lock (_pendingLock)
        {
            if (!CanHandle(frame))
            {
                throw new InvalidDataException("The Worker response has no matching upload request.");
            }

            ready = _ready;
            completion = _completion;
        }

        _channel.ValidateOperationResponse(frame, _rootKey, _operationId);
        if (frame.MessageType == "UploadReady")
        {
            Guid streamId = frame.Payload.GetProperty("streamId").GetGuid();
            _streamId = streamId;
            ready!.TrySetResult(streamId);
            return ValueTask.CompletedTask;
        }

        if (frame.MessageType == "UploadComplete")
        {
            string revision = frame.Payload.GetProperty("revision").GetString()
                ?? throw new InvalidDataException("The Worker upload revision is missing.");
            completion!.TrySetResult(revision);
            return ValueTask.CompletedTask;
        }

        if (frame.MessageType == "OperationError")
        {
            string code = frame.Payload.GetProperty("code").GetString() ?? "Unknown";
            Exception exception = code == "RemoteConflict"
                ? new MirrorPulseWorkerMutationConflictException(
                    frame.Payload.TryGetProperty("expectedRevision", out JsonElement expected) ? expected.GetString() : null,
                    frame.Payload.TryGetProperty("actualRevision", out JsonElement actual) ? actual.GetString() : null)
                : new IOException($"The Adapter Worker upload failed: {code}.");
            ready!.TrySetException(exception);
            completion!.TrySetException(exception);
            return ValueTask.CompletedTask;
        }

        throw new InvalidDataException("The Worker returned an unexpected upload response.");
    }

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _ready?.TrySetException(new IOException("The Adapter Worker disconnected."));
            _completion?.TrySetException(new IOException("The Adapter Worker disconnected."));
        }
    }
}
