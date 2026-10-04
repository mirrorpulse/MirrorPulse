using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Reads one remote revision through a connected Adapter Worker.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "The owning Supervisor disposes the per-instance operation gate after the pipe closes.")]
public sealed class AdapterWorkerStatClient
{
    private readonly AdapterWorkerReadRangeClient _channel;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _pendingLock = new();
    private TaskCompletionSource<string?>? _completion;
    private Guid _requestId;
    private bool _closed;

    public AdapterWorkerStatClient(AdapterWorkerReadRangeClient channel,
        InstanceId instanceId, WorkerSessionId sessionId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _instanceId = instanceId;
        _sessionId = sessionId;
    }

    public async ValueTask<string?> StatAsync(
        MirrorPulseWorkerStatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId != _instanceId)
        {
            throw new ArgumentException("The stat request belongs to another instance.", nameof(request));
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _requestId = requestId;
                _completion = completion;
            }

            await _channel.WriteControlAsync(new ControlFrameEnvelope(1, "Stat", requestId,
                _instanceId, _sessionId, false,
                JsonSerializer.SerializeToElement(new { path = request.NormalizedPath })), cancellationToken)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_completion, completion))
                {
                    _completion = null;
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
        TaskCompletionSource<string?> completion;
        lock (_pendingLock)
        {
            if (!CanHandle(frame))
            {
                throw new InvalidDataException("The Worker response has no matching stat request.");
            }

            completion = _completion!;
        }

        if (frame.MessageType == "StatResult")
        {
            string? revision = frame.Payload.TryGetProperty("revision", out JsonElement value) &&
                value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? value.GetString()
                : null;
            completion.TrySetResult(revision);
            return ValueTask.CompletedTask;
        }

        if (frame.MessageType == "OperationError")
        {
            string code = frame.Payload.GetProperty("code").GetString() ?? "Unknown";
            completion.TrySetException(new AdapterWorkerOperationException(code));
            return ValueTask.CompletedTask;
        }

        throw new InvalidDataException("The Worker returned an unexpected stat response.");
    }

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _completion?.TrySetException(new AdapterWorkerOperationException("Disconnected"));
        }
    }
}
