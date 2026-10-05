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
            }

            await WriteControlAsync(new ControlFrameEnvelope(1, "ReadRange", requestId,
                _instanceId, _sessionId, false,
                JsonSerializer.SerializeToElement(new
                {
                    path = request.NormalizedPath,
                    offset = request.Offset,
                    length = request.Length,
                })), cancellationToken).ConfigureAwait(false);
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

        byte[] payload = BinaryChunkCodec.Encode(chunk);
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

            BinaryChunkFrame chunk = BinaryChunkCodec.Decode(
                await LengthPrefixedFrameReader.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false));
            if (chunk.RequestId != frame.RequestId || chunk.InstanceId != _instanceId ||
                chunk.WorkerSessionId != _sessionId || chunk.StreamId != streamId ||
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
