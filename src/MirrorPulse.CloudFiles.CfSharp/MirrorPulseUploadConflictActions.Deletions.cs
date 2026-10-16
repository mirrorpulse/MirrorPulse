using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseUploadConflictActions
{
    private async Task PrepareDeletionAdmittedAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseJournalCoalescingPlan? group, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireDeletionService();
        var route = _router!.ResolvePath(conflict.RelativePath);
        var root = _router.GetRegistration(route.InstanceId, route.RootKey);
        if (route.InstanceId != conflict.InstanceId || route.RelativePath.Length == 0 || root.State != RootRegistrationState.Active || !_mayDispatch(route.InstanceId))
            throw new InvalidOperationException("A destructive conflict decision requires its enabled managed root and Worker.");
        using var admission = _namespaceWork?.Enter([root.RootId]);
        Guid originalId = Guid.Parse(conflict.ChangeId);
        Guid itemId;
        await using (var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var original = await transaction.Operations.GetAsync(originalId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The conflict lost its original official journal operation.");
            itemId = original.ItemId ?? throw new InvalidDataException("Conflict deletion requires the original logical item identity.");
            if (group is not null)
                foreach (var member in group.Members)
                {
                    var retained = await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false);
                    if (retained?.ItemId != group.ItemId || retained.Sequence != member.Sequence || group.ItemId != itemId)
                        throw new InvalidDataException("Deletion cannot replace any original official group member.");
                }
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        var content = await _localDeletion!.CaptureAsync(conflict, itemId, token).ConfigureAwait(false);
        _ = await _catalog.PrepareConflictDeletionAsync(commandId, conflict.ConflictId, route.RootKey, itemId,
            conflict.RelativePath, content, token).ConfigureAwait(false);
    }

    internal async ValueTask<MirrorPulseConflictResolution> ResumeConflictDeletionAsync(Guid commandId, CancellationToken token)
    {
        var decision = await _catalog.ReadConflictDeletionAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The conflict deletion decision is missing.");
        return _scheduler is null ? await ExecuteDeletionAdmittedAsync(commandId, token).ConfigureAwait(false) :
            await _scheduler.RunAsync(decision.Intent.InstanceId, stop => ExecuteDeletionAdmittedAsync(commandId, stop), token).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseConflictResolution> ExecuteDeletionAdmittedAsync(Guid commandId, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireDeletionService();
        var decision = await _catalog.ReadConflictDeletionAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retained conflict deletion decision is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        if (root.State != RootRegistrationState.Active || !_mayDispatch(root.InstanceId))
            throw new InvalidOperationException("An offline root retains its deletion decision without accessing local or remote content.");
        await using var admission = _namespaceWork is null ? null : await _namespaceWork.EnterPendingOperationAsync(root.RootId, token).ConfigureAwait(false);
        var executor = new MirrorPulseConflictDeletionExecutor(_catalog, _mutations!, new(_stats!));
        var completed = await executor.ExecuteAsync(commandId, (deletion, stop) =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
                return new(_localDeletion!.CompleteAsync(deletion, stop));
            },
            AcknowledgeDeletedOriginalsAsync, token).ConfigureAwait(false);
        foreach (Guid id in completed.Intent.ConflictIds) _conflictCenter?.Remove(id);
        return new(commandId, completed.Intent.ConflictId, MirrorPulseConflictAction.DeleteRemote,
            await _localDeletion!.ReadPreservedPathAsync(completed.Intent, token).ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    private async ValueTask AcknowledgeDeletedOriginalsAsync(MirrorPulseConflictDeletionIntent intent, CancellationToken token)
    {
        await using (var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            foreach (Guid originalId in intent.OriginalOperationIds)
            {
                var original = await transaction.Operations.GetAsync(originalId, token).ConfigureAwait(false);
                if (original is not null && original.ItemId != intent.ItemId)
                    throw new InvalidDataException("The retained deletion cannot acknowledge a different official logical item.");
            }
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        await _feed.AcknowledgeAsync(intent.OriginalOperationIds.Select(id => new CloudLocalChangeAcknowledgement(id, null)), token).ConfigureAwait(false);
        await using var checking = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (Guid id in intent.OriginalOperationIds)
            if (await checking.Operations.GetAsync(id, token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("The complete original group was not acknowledged by the official feed.");
        await checking.RollbackAsync(token).ConfigureAwait(false);
    }

    private void RequireDeletionService()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || _localDeletion is null || _router is null || _mutations is null || _stats is null)
            throw new NotSupportedException("Deleting remote conflict content requires the protected current-user Host and its conditional Worker boundary.");
    }
}
