using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Host;

/// <summary>Correlates bounded hydration reads with one connected Worker session.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "Close faults in-flight reads; releasing semaphores after their continuations is unsafe.")]
public sealed class AdapterWorkerReadRangeClient
{
    public const int MaximumRangeBytes = 1024 * 1024;

    private readonly Stream _pipe;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _pendingLock = new();
    private TaskCompletionSource<Stream>? _pending;
    private Guid _requestId;
    private long _offset;
    private long _length;
    private string? _rootKey;
    private bool _closed;

    public AdapterWorkerReadRangeClient(Stream pipe, InstanceId instanceId, WorkerSessionId sessionId,
        AdapterWorkerProtocolSession? protocol = null)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _instanceId = instanceId;
        _sessionId = sessionId;
        Protocol = protocol;
    }

    public AdapterWorkerProtocolSession? Protocol { get; }
    public int ProtocolVersion => Protocol?.ProtocolVersion ?? 1;
    public JsonElement RoutePayload(string? rootKey, JsonElement payload) => Protocol?.RoutePayload(rootKey, payload) ?? payload;
    public void ValidateResponse(ControlFrameEnvelope frame, string? rootKey)
    {
        if (Protocol is not null) Protocol.ValidateResponse(frame, rootKey);
        else if (!frame.IsResponse || frame.ProtocolVersion != 1) throw new InvalidDataException("ResponseProtocolMismatch");
    }

    public JsonElement MutationPayload(string? rootKey, object payload, Guid operationId,
        string? destinationRootKey = null, string? expectedRevision = null, bool destinationMustBeAbsent = true)
    {
        JsonElement routed = RoutePayload(rootKey, JsonSerializer.SerializeToElement(payload));
        if (ProtocolVersion == 1)
        {
            if (destinationRootKey is not null && destinationRootKey != rootKey)
                throw new NotSupportedException("CrossRootMoveRequiresProtocolV2");
            return routed;
        }
        if (operationId == Guid.Empty) throw new InvalidDataException("OperationIdRequired");
        Dictionary<string, JsonElement> fields = routed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["operationId"] = JsonSerializer.SerializeToElement(operationId);
        fields["preconditions"] = JsonSerializer.SerializeToElement(new { expectedRevision, destinationMustBeAbsent });
        if (fields.TryGetValue("sourcePath", out JsonElement source)) fields["path"] = source;
        if (destinationRootKey is not null)
        {
            _ = RoutePayload(destinationRootKey, JsonSerializer.SerializeToElement(new { }));
            fields["destinationRootKey"] = JsonSerializer.SerializeToElement(destinationRootKey);
        }
        return JsonSerializer.SerializeToElement(fields);
    }

    public void ValidateOperationResponse(ControlFrameEnvelope frame, string? rootKey, Guid operationId)
    {
        ValidateResponse(frame, rootKey);
        if (ProtocolVersion == 2 && (!frame.Payload.TryGetProperty("operationId", out JsonElement id) ||
            !id.TryGetGuid(out Guid actual) || actual != operationId))
            throw new InvalidDataException("ResponseOperationMismatch");
    }

    public async ValueTask<Stream> ReadRangeAsync(
        MirrorPulseWorkerReadRangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId != _instanceId || request.Offset < 0 ||
            request.Length is < 1 or > MaximumRangeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The Worker range or instance is invalid.");
        }

        JsonElement payload = RoutePayload(request.RootKey, JsonSerializer.SerializeToElement(new
        {
            path = request.NormalizedPath,
            offset = request.Offset,
            length = request.Length,
        }));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<Stream> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = Guid.NewGuid();
        bool dispatched = false;
        bool delivered = false;
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _pending = completion;
                _requestId = requestId;
                _offset = request.Offset;
                _length = request.Length;
                _rootKey = request.RootKey;
            }

            await WriteControlAsync(new ControlFrameEnvelope(1, "ReadRange", requestId,
                _instanceId, _sessionId, false,
                payload), cancellationToken).ConfigureAwait(false);
            dispatched = true;
            Stream result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            delivered = true;
            return result;
        }
        finally
        {
            bool drain = false;
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, completion))
                {
                    if (!completion.Task.IsCompleted && dispatched && !_closed)
                    {
                        // The consumer can leave, but the response reader must still
                        // correlate and validate its bounded frame. Hold this range
                        // gate until that frame is drained or the session closes.
                        drain = true;
                    }
                    else
                    {
                        _pending = null;
                        if (!completion.Task.IsCompleted) _closed = true;
                    }
                }
            }
            if (drain) _ = DrainAbandonedReadAsync(completion);
            else
            {
                if (!delivered && completion.Task.IsCompletedSuccessfully) completion.Task.Result.Dispose();
                _operation.Release();
            }
        }
    }

    private async Task DrainAbandonedReadAsync(TaskCompletionSource<Stream> completion)
    {
        try { (await completion.Task.ConfigureAwait(false)).Dispose(); }
        catch (Exception) { /* The session reader reports failures; this observes the abandoned result. */ }
        finally
        {
            lock (_pendingLock)
                if (ReferenceEquals(_pending, completion)) _pending = null;
            _operation.Release();
        }
    }

    public async ValueTask WriteControlAsync(
        ControlFrameEnvelope frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.ProtocolVersion != ProtocolVersion)
            frame = new(ProtocolVersion, frame.MessageType, frame.RequestId, frame.InstanceId, frame.WorkerSessionId, frame.IsResponse, frame.Payload);
        byte[] payload = ControlFrameJsonCodec.Encode(frame);
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async ValueTask WriteChunkAsync(
        BinaryChunkFrame chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.InstanceId != _instanceId || chunk.WorkerSessionId != _sessionId)
        {
            throw new InvalidDataException("The binary chunk belongs to another Worker session.");
        }

        byte[] payload = ProtocolVersion == 2 ? WorkerBinaryChunkV2Codec.Encode(chunk) : BinaryChunkCodec.Encode(chunk);
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    public bool CanHandle(ControlFrameEnvelope frame)
    {
        lock (_pendingLock)
        {
            return !_closed && _pending is not null && frame.RequestId == _requestId &&
                frame.InstanceId == _instanceId && frame.WorkerSessionId == _sessionId;
        }
    }

    /// <summary>Called only by the session's single pipe reader.</summary>
    public async ValueTask HandleResponseAsync(
        ControlFrameEnvelope frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TaskCompletionSource<Stream> completion;
        long offset;
        long length;
        lock (_pendingLock)
        {
            if (_closed || _pending is null || frame.RequestId != _requestId ||
                frame.InstanceId != _instanceId || frame.WorkerSessionId != _sessionId ||
                !frame.IsResponse || frame.ProtocolVersion != ProtocolVersion)
            {
                throw new InvalidDataException("The Worker response has no matching range request.");
            }

            completion = _pending;
            offset = _offset;
            length = _length;
        }

        try
        {
            ValidateResponse(frame, _rootKey);
            if (frame.MessageType == "OperationError")
            {
                string code = frame.Payload.GetProperty("code").GetString() ?? "Unknown";
                completion.TrySetException(new IOException($"The Adapter range read failed: {code}."));
                return;
            }

            if (frame.MessageType != "ReadRangeReady")
            {
                throw new InvalidDataException("The Worker returned an unexpected range response.");
            }

            Guid streamId = frame.Payload.GetProperty("streamId").GetGuid();
            long declaredLength = frame.Payload.GetProperty("length").GetInt64();
            if (streamId == Guid.Empty || declaredLength != length)
            {
                throw new InvalidDataException("The Worker declared a different range length.");
            }

            byte[] bytes = await LengthPrefixedFrameReader.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
            BinaryChunkFrame chunk = ProtocolVersion == 2 ? WorkerBinaryChunkV2Codec.Decode(bytes) : BinaryChunkCodec.Decode(bytes);
            if (chunk.RequestId != frame.RequestId || chunk.InstanceId != _instanceId ||
                chunk.WorkerSessionId != _sessionId || chunk.StreamId != streamId ||
                (ProtocolVersion == 2 && chunk.RootKey != _rootKey) ||
                chunk.Offset != offset || chunk.Data.Length != length || !chunk.EndOfStream ||
                chunk.Sha256 is not { } digest ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(digest.Hexadecimal), SHA256.HashData(chunk.Data.Span)))
            {
                throw new InvalidDataException("The Worker returned an invalid range chunk.");
            }

            completion.TrySetResult(new MemoryStream(chunk.Data.ToArray(), writable: false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
    }

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _pending?.TrySetException(new IOException("The Adapter Worker disconnected."));
        }
    }
}
