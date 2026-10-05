using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Correlates one remote move or delete request with a connected Worker.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "The owning Supervisor disposes the per-instance operation gate after the pipe closes.")]
public sealed class AdapterWorkerMutationClient
{
    private readonly AdapterWorkerReadRangeClient _channel;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _pendingLock = new();
    private TaskCompletionSource<string?>? _completion;
    private Guid _requestId;
    private Guid _operationId;
    private string? _rootKey;
    private bool _closed;

    public AdapterWorkerMutationClient(
        AdapterWorkerReadRangeClient channel,
        InstanceId instanceId,
        WorkerSessionId sessionId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _instanceId = instanceId;
        _sessionId = sessionId;
    }

    public ValueTask<string?> DeleteAsync(
        MirrorPulseWorkerDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync("Delete", new
        {
            path = request.NormalizedPath,
            expectedRevision = request.ExpectedRevision,
            isDirectory = request.IsDirectory,
        }, request.OperationId, request.RootKey, null, request.ExpectedRevision, true, cancellationToken);

    public async ValueTask<string> MoveAsync(
        MirrorPulseWorkerMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync("Move", new
        {
            sourcePath = request.SourcePath,
            destinationPath = request.DestinationPath,
            expectedRevision = request.ExpectedRevision,
            isDirectory = request.IsDirectory,
        }, request.OperationId, request.RootKey, request.DestinationRootKey ?? request.RootKey,
            request.ExpectedRevision, request.DestinationMustBeAbsent, cancellationToken).ConfigureAwait(false) ??
            throw new IOException("The Adapter Worker did not return a moved revision.");
    }

    public async ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId != _instanceId) throw new InvalidDataException("InstanceMismatch");
        if (_channel.ProtocolVersion != 2) throw new NotSupportedException("CreateDirectoryRequiresProtocolV2");
        return await SendAsync("CreateDirectory", new { path = request.NormalizedPath, mustBeAbsent = request.MustBeAbsent },
            request.OperationId, request.RootKey, null, null, request.MustBeAbsent, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DirectoryRevisionRequired");
    }

    private async ValueTask<string?> SendAsync(
        string messageType,
        object payload,
        Guid? operationId,
        string? rootKey,
        string? destinationRootKey,
        string? expectedRevision,
        bool destinationMustBeAbsent,
        CancellationToken cancellationToken)
    {
        Guid stableOperationId = operationId ?? Guid.NewGuid();
        if (stableOperationId == Guid.Empty) throw new ArgumentException("The Worker operation ID must not be empty.", nameof(operationId));
        Guid requestId = _channel.ProtocolVersion == 2 ? Guid.NewGuid() : stableOperationId;
        if (requestId == Guid.Empty) throw new ArgumentException("The Worker operation ID must not be empty.", nameof(operationId));
        JsonElement routedPayload = _channel.MutationPayload(rootKey, payload, stableOperationId,
            destinationRootKey, expectedRevision, destinationMustBeAbsent);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _requestId = requestId;
                _operationId = stableOperationId;
                _rootKey = rootKey;
                _completion = completion;
            }

            await _channel.WriteControlAsync(new ControlFrameEnvelope(1, messageType, requestId,
                _instanceId, _sessionId, false, routedPayload),
                cancellationToken).ConfigureAwait(false);
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
                throw new InvalidDataException("The Worker response has no matching mutation request.");
            }

            completion = _completion!;
        }

        _channel.ValidateOperationResponse(frame, _rootKey, _operationId);
        if (frame.MessageType == "MutationComplete")
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
            if (code == "RemoteConflict")
            {
                string? expected = frame.Payload.TryGetProperty("expectedRevision", out JsonElement expectedValue)
                    ? expectedValue.GetString() : null;
                string? actual = frame.Payload.TryGetProperty("actualRevision", out JsonElement actualValue)
                    ? actualValue.GetString() : null;
                completion.TrySetException(new MirrorPulseWorkerMutationConflictException(expected, actual));
            }
            else
            {
                completion.TrySetException(new IOException($"The Adapter Worker mutation failed: {code}."));
            }

            return ValueTask.CompletedTask;
        }

        throw new InvalidDataException("The Worker returned an unexpected mutation response.");
    }

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _completion?.TrySetException(new IOException("The Adapter Worker disconnected."));
        }
    }
}
