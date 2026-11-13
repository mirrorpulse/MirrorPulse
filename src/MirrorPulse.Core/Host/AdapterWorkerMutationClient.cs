using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Correlates one remote mutation at a time with a connected Worker.</summary>
[SuppressMessage("Design", "CA1001", Justification =
    "Close faults pending mutations and lets callers drain the managed-only operation gate.")]
public sealed class AdapterWorkerMutationClient
{
    private readonly AdapterWorkerReadRangeClient _channel;
    private readonly InstanceId _instanceId;
    private readonly WorkerSessionId _sessionId;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _pendingLock = new();
    private PendingMutation? _pending;
    private bool _closed;

    private sealed record PendingMutation(ControlFrameEnvelope Request, Guid OperationId, string? RootKey)
    {
        public TaskCompletionSource<MutationReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct MutationReply(string? Revision, AdapterOwnedChildEffectReceipt? DirectoryReceipt = null)
    {
#if HOST_ATOMIC_DIRECTORY_CANDIDATE
        internal AdapterDirectoryAdmission? Admission { get; init; }
        internal AdapterAtomicDirectoryDeletionReceipt? AtomicReceipt { get; init; }
#endif
    }

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

    public async ValueTask<AdapterOwnedChildEffectReceipt> RetireOriginalDirectoryAsync(
        AdapterDirectoryRetirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var protocol = _channel.Protocol ?? throw new NotSupportedException("OriginalDirectoryEffectsUnsupported");
        AdapterDirectoryEffects.RequireNegotiated(protocol.ProtocolVersion, protocol.Capabilities);
        var frame = new ControlFrameEnvelope(protocol.ProtocolVersion, AdapterDirectoryRetirementWire.RequestMessage,
            Guid.NewGuid(), _instanceId, _sessionId, false, AdapterProtocolJson.ToElement(request));
        var retained = AdapterDirectoryRetirementWire.ReadRequest(ToSdkFrame(frame), protocol.Capabilities);
        protocol.RequireActiveRoot(retained.Operation.RootKey);
        if (retained.Operation.DestinationRootKey is { } destinationRoot)
            protocol.RequireActiveRoot(destinationRoot);
        var pending = new PendingMutation(frame, retained.Operation.OperationId, retained.Operation.RootKey);
        var reply = await SendCoreAsync(pending, cancellationToken).ConfigureAwait(false);
        return reply.DirectoryReceipt ?? throw new InvalidDataException("DirectoryRetirementReceiptRequired");
    }

#if HOST_ATOMIC_DIRECTORY_CANDIDATE
    internal bool SupportsAtomicDirectoryDeletion => _channel.Protocol is { ProtocolVersion: 2 } protocol &&
        protocol.Capabilities.Contains(AdapterOriginalDirectoryWire.Capability, StringComparer.Ordinal) &&
        protocol.Capabilities.Contains(AdapterAtomicDirectoryDeletionWire.Capability, StringComparer.Ordinal);

    internal void RequireAtomicDirectorySupport(AdapterDirectoryAdmissionRequest request)
    {
        var protocol = _channel.Protocol;
        if (protocol is null || !SupportsAtomicDirectoryDeletion)
            throw new NotSupportedException("AtomicDirectoryDeletionUnsupported");
        if (request.InstanceId != _instanceId.Value) throw new InvalidDataException("InstanceMismatch");
        request.Validate();
        foreach (var step in request.Operations)
        {
            protocol.RequireActiveRoot(step.Operation.RootKey);
            if (step.OperationKind != "Delete") throw new NotSupportedException("AtomicDirectoryDeletionRequiresDeletePlan");
        }
    }

    internal async ValueTask<AdapterDirectoryAdmission> AdmitAtomicDirectoryAsync(AdapterDirectoryAdmissionRequest request, CancellationToken token)
    {
        RequireAtomicDirectorySupport(request);
        var operation = request.Operations[^1].Operation;
        var frame = new ControlFrameEnvelope(2, AdapterOriginalDirectoryWire.AdmissionRequestMessage, Guid.NewGuid(),
            _instanceId, _sessionId, false, AdapterProtocolJson.ToElement(request));
        var reply = await SendCoreAsync(new(frame, operation.OperationId, operation.RootKey), token).ConfigureAwait(false);
        return reply.Admission ?? throw new InvalidDataException("OriginalDirectoryAdmissionRequired");
    }

    internal async ValueTask<AdapterAtomicDirectoryDeletionReceipt> DeleteAtomicDirectoryAsync(AdapterAtomicDirectoryDeletionRequest request,
        CancellationToken token)
    {
        RequireAtomicDirectorySupport(request.OriginalRequest);
        var operation = request.OriginalRequest.Operations[^1].Operation;
        var frame = new ControlFrameEnvelope(2, AdapterAtomicDirectoryDeletionWire.RequestMessage, Guid.NewGuid(),
            _instanceId, _sessionId, false, AdapterProtocolJson.ToElement(request));
        var reply = await SendCoreAsync(new(frame, operation.OperationId, operation.RootKey), token).ConfigureAwait(false);
        return reply.AtomicReceipt ?? throw new InvalidDataException("AtomicDirectoryDeletionReceiptRequired");
    }
#endif

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
        var frame = new ControlFrameEnvelope(_channel.ProtocolVersion, messageType, requestId,
            _instanceId, _sessionId, false, routedPayload);
        var reply = await SendCoreAsync(new PendingMutation(frame, stableOperationId, rootKey), cancellationToken)
            .ConfigureAwait(false);
        return reply.Revision;
    }

    private async ValueTask<MutationReply> SendCoreAsync(PendingMutation pending, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _pending = pending;
            }

            await _channel.WriteControlAsync(pending.Request, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    if (!pending.Completion.Task.IsCompleted)
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
            return !_closed && _pending is { } pending && frame.RequestId == pending.Request.RequestId &&
                frame.InstanceId == _instanceId && frame.WorkerSessionId == _sessionId;
        }
    }

    public ValueTask HandleResponseAsync(ControlFrameEnvelope frame)
    {
        PendingMutation pending;
        lock (_pendingLock)
        {
            if (!CanHandle(frame))
            {
                throw new InvalidDataException("The Worker response has no matching mutation request.");
            }

            pending = _pending!;
        }

        var completion = pending.Completion;
#if HOST_ATOMIC_DIRECTORY_CANDIDATE
        if (pending.Request.MessageType is AdapterOriginalDirectoryWire.AdmissionRequestMessage or AdapterAtomicDirectoryDeletionWire.RequestMessage)
        {
            if (frame.MessageType == "OperationError")
            {
                if (!frame.IsResponse || frame.ProtocolVersion != 2)
                    throw new InvalidDataException("AtomicDirectoryResponseProtocolMismatch");
                // Typed requests carry their addresses inside the body. Local emits
                // a correlated envelope error without a separate root or operation.
                string error = frame.Payload.GetProperty("code").GetString() ?? "Unknown";
                completion.TrySetException(new AdapterWorkerOperationException(error));
            }
            else if (pending.Request.MessageType == AdapterOriginalDirectoryWire.AdmissionRequestMessage)
            {
                var admission = AdapterOriginalDirectoryWire.ReadAdmissionResponse(ToSdkFrame(pending.Request), ToSdkFrame(frame),
                    _channel.Protocol?.Capabilities);
                completion.TrySetResult(new MutationReply(null) { Admission = admission });
            }
            else
            {
                var receipt = AdapterAtomicDirectoryDeletionWire.ReadResponse(ToSdkFrame(pending.Request), ToSdkFrame(frame),
                    _channel.Protocol?.Capabilities);
                completion.TrySetResult(new MutationReply(null) { AtomicReceipt = receipt });
            }
            return ValueTask.CompletedTask;
        }
#endif
        if (pending.Request.MessageType == AdapterDirectoryRetirementWire.RequestMessage && frame.MessageType != "OperationError")
        {
            var receipt = AdapterDirectoryRetirementWire.ReadResponse(ToSdkFrame(pending.Request), ToSdkFrame(frame),
                _channel.Protocol?.Capabilities);
            completion.TrySetResult(new MutationReply(receipt.AcceptedRevision, receipt));
            return ValueTask.CompletedTask;
        }

        _channel.ValidateOperationResponse(frame, pending.RootKey, pending.OperationId);
        if (frame.MessageType == "MutationComplete")
        {
            string? revision = frame.Payload.TryGetProperty("revision", out JsonElement value) &&
                value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? value.GetString()
                : null;
            completion.TrySetResult(new MutationReply(revision));
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
            else if (code is "OperationUnsupported" or "CreateDirectoryUnsupported" or "CapabilityUnavailable")
                completion.TrySetException(new NotSupportedException("The Worker rejected an unsupported mutation before execution."));
            else
            {
                completion.TrySetException(new AdapterWorkerOperationException(code));
            }

            return ValueTask.CompletedTask;
        }

        throw new InvalidDataException("The Worker returned an unexpected mutation response.");
    }

    private static AdapterControlFrame ToSdkFrame(ControlFrameEnvelope frame) => new(frame.ProtocolVersion, frame.MessageType,
        frame.RequestId, frame.InstanceId.Value, frame.WorkerSessionId.Value, frame.IsResponse, frame.Payload);

    public void Close()
    {
        lock (_pendingLock)
        {
            _closed = true;
            _pending?.Completion.TrySetException(new IOException("The Adapter Worker disconnected."));
        }
    }
}
