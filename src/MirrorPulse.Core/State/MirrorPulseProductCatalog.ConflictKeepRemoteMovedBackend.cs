using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the acknowledged original Move immediately preceding this command's rejected content effect.</summary>
    /// <remarks>This retains original versions and ownership; it neither selects a backend object nor authorizes native work.</remarks>
    public async Task<MirrorPulseMutationRecord?> ReadConflictKeepRemoteAcknowledgedMoveAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original KeepRemote command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original KeepRemote preparation is missing.");
            var intent = retained.Intent;
            var plan = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
            var original = await ReadCoalescingExecutionCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original KeepRemote execution is missing.");
            var dispatch = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, token).ConfigureAwait(false);
            if (dispatch.Steps.Count != 2 || dispatch.Steps[0].Kind != MirrorPulseCoalescingStepKind.Move ||
                dispatch.Steps[1].Kind != MirrorPulseCoalescingStepKind.Upload)
            {
                transaction.Commit(); return null;
            }
            var movedIntent = await ReadCurrentCoalescingIntentAsync(plan, dispatch, 0, dispatch.ExpectedRevision, transaction, token).ConfigureAwait(false);
            var moved = await ReadMutationCoreAsync(movedIntent.OperationId, token, transaction).ConfigureAwait(false);
            if (moved is not { State: MirrorPulseMutationState.Acknowledged, AcceptedRevision.Length: > 0 } ||
                !SameIntent(moved.Intent, movedIntent) || movedIntent.Kind != MirrorPulseWorkerChangeKind.Move || movedIntent.IsDirectory ||
                movedIntent.InstanceId != intent.InstanceId || movedIntent.RootKey != intent.RemoteSource.RootKey ||
                string.IsNullOrWhiteSpace(movedIntent.PreviousRelativePath) || movedIntent.RelativePath != intent.RemoteSource.RelativePath)
                throw new InvalidDataException("KeepRemote requires its exact acknowledged original Move prefix.");
            var rejected = await ReadCurrentCoalescingIntentAsync(plan, dispatch, 1, moved.AcceptedRevision, transaction, token).ConfigureAwait(false);
            if (!SameIntent(rejected, intent.RejectedEffect) || rejected.Kind != MirrorPulseWorkerChangeKind.ContentUpdate ||
                rejected.IsDirectory || rejected.ExpectedRevision != moved.AcceptedRevision || rejected.RelativePath != movedIntent.RelativePath)
                throw new InvalidDataException("KeepRemote lost the rejected content effect following its acknowledged Move.");
            transaction.Commit(); return moved;
        }
        finally { _gate.Release(); }
    }
}
