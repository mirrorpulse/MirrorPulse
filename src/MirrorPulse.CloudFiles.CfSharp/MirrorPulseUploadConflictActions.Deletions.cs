using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseUploadConflictActions
{
    private async ValueTask<MirrorPulseConflictResolution> ApplyExistingDeletionAsync(Guid decisionId,
        MirrorPulseConflictRecord conflict, RootId? admittedRoot, CancellationToken token)
    {
        var decision = await _catalog.ReadConflictDeletionAsync(decisionId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original conflict deletion is missing.");
        var effect = await _catalog.ReadCurrentConflictDeletionEffectAsync(decisionId, token).ConfigureAwait(false);
        var current = await _catalog.ReadMutationAsync(effect.OperationId, token).ConfigureAwait(false);
        if (decision.Phase != MirrorPulseConflictDeletionPhase.Retired || current?.State != MirrorPulseMutationState.Conflict ||
            await _catalog.ReadConflictDeletionObservationAsync(effect.OperationId, token).ConfigureAwait(false) is null)
            return await ExecuteDeletionAdmittedAsync(decisionId, admittedRoot, token).ConfigureAwait(false);
        RequireDeletionService();
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        if (root.State != RootRegistrationState.Active || !_mayDispatch(root.InstanceId))
            throw new InvalidOperationException("An offline root retains its deletion choice without sending a new effect.");
        Guid commandId = Guid.NewGuid();
        await _catalog.SaveUserCommandAsync(new(commandId, "deleteremote", conflict.ConflictId.ToString("D"), "pending"), token).ConfigureAwait(false);
        try
        {
            _ = await _catalog.AuthorizeConflictDeletionRetryAsync(commandId, decisionId, conflict.ConflictId, token).ConfigureAwait(false);
            return (await ExecuteDeletionAdmittedAsync(decisionId, admittedRoot, token).ConfigureAwait(false)) with { ResolutionId = commandId };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if ((await _catalog.ReadUserCommandAsync(commandId, CancellationToken.None).ConfigureAwait(false))?.State == "pending")
                await _catalog.SaveUserCommandAsync(new(commandId, "deleteremote", conflict.ConflictId.ToString("D"), "failed"), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PrepareDeletionAdmittedAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseJournalCoalescingPlan? group, RootId? admittedRoot, bool localOnly, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        if (localOnly) RequireLocalRemovalService(); else RequireDeletionService();
        var route = _router!.ResolvePath(conflict.RelativePath);
        var root = _router.GetRegistration(route.InstanceId, route.RootKey);
        if (route.InstanceId != conflict.InstanceId || route.RelativePath.Length == 0 ||
            (localOnly ? root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) :
                root.State != RootRegistrationState.Active || !_mayDispatch(route.InstanceId)))
            throw new InvalidOperationException("A destructive conflict decision requires its enabled managed root and Worker.");
        using var admission = admittedRoot == root.RootId ? null : _namespaceWork?.Enter([root.RootId]);
        Guid originalId = Guid.Parse(conflict.ChangeId);
        Guid itemId;
        long originalSequence;
        await using (var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var original = await transaction.Operations.GetAsync(originalId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The conflict lost its original official journal operation.");
            itemId = original.ItemId ?? throw new InvalidDataException("Conflict deletion requires the original logical item identity.");
            originalSequence = original.Sequence;
            if (group is not null)
                foreach (var member in group.Members)
                {
                    var retained = await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false);
                    if (retained?.ItemId != group.ItemId || retained.Sequence != member.Sequence || group.ItemId != itemId)
                        throw new InvalidDataException("Deletion cannot replace any original official group member.");
                }
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        MirrorPulseWorkerChangeCommand? observation = null;
        if (group is null)
        {
            // Seek by the official sequence into one bounded public page. This
            // neither parses private journal payloads nor scans disabled backlog.
            var scan = await _feed.BeginScanAsync(token).ConfigureAwait(false);
            if (scan.RequiresFullRescan || originalSequence <= 0 || originalSequence > scan.ThroughSequence)
                throw new InvalidDataException("The original conflict has no current bounded public journal observation.");
            var page = await _feed.ReadPageAsync(scan, originalSequence - 1, 1, token).ConfigureAwait(false);
            var change = page.Changes.SingleOrDefault(value => value.OperationId == originalId && value.Sequence == originalSequence);
            if (page.RequiresFullRescan || change is null || change.ItemId != itemId)
                throw new InvalidDataException("The original public conflict observation disappeared or changed identity.");
            var mapped = MirrorPulseLocalBatchMapper.MapObservations([new(change.OperationId, change.Sequence, change.Kind, change.ItemId,
                change.RelativePath, change.PreviousRelativePath, change.IsDirectory, change.ObservedAt)], _router);
            observation = mapped.Commands.SingleOrDefault();
            if (observation is null || observation.InstanceId != route.InstanceId || observation.RootKey != route.RootKey ||
                observation.RelativePath != route.RelativePath || observation.IsDirectory)
                throw new InvalidDataException("The original public file observation does not match the conflict's routed local object.");
        }
        var content = await _localDeletion!.CaptureAsync(conflict, itemId, token).ConfigureAwait(false);
        if (localOnly)
            _ = await _catalog.PrepareConflictLocalRemovalAsync(commandId, conflict.ConflictId, route.RootKey, itemId,
                conflict.RelativePath, content, observation, token).ConfigureAwait(false);
        else if (observation is null)
            _ = await _catalog.PrepareConflictDeletionAsync(commandId, conflict.ConflictId, route.RootKey, itemId,
                conflict.RelativePath, content, token).ConfigureAwait(false);
        else
            _ = await _catalog.PrepareConflictDeletionFromObservationAsync(commandId, conflict.ConflictId, route.RootKey, itemId,
                conflict.RelativePath, content, observation, token).ConfigureAwait(false);
    }

    internal async ValueTask<MirrorPulseConflictResolution> ResumeConflictDeletionAsync(Guid commandId, CancellationToken token)
    {
        var decision = await _catalog.ReadConflictDeletionAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The conflict deletion decision is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        await using var admission = _namespaceWork is null ? null : await _namespaceWork.EnterPendingOperationAsync(root.RootId, token).ConfigureAwait(false);
        return _scheduler is null ? await ExecuteDeletionAdmittedAsync(commandId, root.RootId, token).ConfigureAwait(false) :
            await _scheduler.RunAsync(decision.Intent.InstanceId, stop => ExecuteDeletionAdmittedAsync(commandId, root.RootId, stop), token).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseConflictResolution> ExecuteDeletionAdmittedAsync(Guid commandId, RootId? admittedRoot, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireDeletionService();
        var decision = await _catalog.ReadConflictDeletionAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retained conflict deletion decision is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        if (root.State != RootRegistrationState.Active || !_mayDispatch(root.InstanceId))
            throw new InvalidOperationException("An offline root retains its deletion decision without accessing local or remote content.");
        if (admittedRoot != root.RootId) throw new InvalidDataException("Conflict deletion lost its explicit root admission.");
        var executor = new MirrorPulseConflictDeletionExecutor(_catalog, _mutations!, new(_stats!));
        MirrorPulseConflictDeletion completed;
        try
        {
            completed = await executor.ExecuteAsync(commandId, (deletion, stop) =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
                return new(_localDeletion!.CompleteAsync(deletion, stop));
            },
            AcknowledgeDeletedOriginalsAsync, token).ConfigureAwait(false);
        }
        catch (MirrorPulseWorkerMutationConflictException)
        {
            await PublishDeletionConflictsAsync(decision.Intent, token).ConfigureAwait(false);
            throw;
        }
        await PublishDeletionConflictsAsync(completed.Intent, token).ConfigureAwait(false);
        return new(commandId, completed.Intent.ConflictId, MirrorPulseConflictAction.DeleteRemote,
            await _localDeletion!.ReadPreservedPathAsync(completed.Intent, token).ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    private async Task PublishDeletionConflictsAsync(MirrorPulseConflictDeletionIntent intent, CancellationToken token)
    {
        await foreach (var conflict in ReadOriginalConflictEpochsAsync(intent.InstanceId, intent.OriginalOperationIds, token).ConfigureAwait(false))
        {
            if (!conflict.IsPending)
            {
                _conflictCenter?.Remove(conflict.ConflictId); _announcedDeletionConflicts.TryRemove(conflict.ConflictId, out _); continue;
            }
            _conflictCenter?.Upsert(conflict);
            if (_conflictNotifications is null || !_announcedDeletionConflicts.TryAdd(conflict.ConflictId, 0)) continue;
            try { await _conflictNotifications.NotifyAsync(conflict, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { _announcedDeletionConflicts.TryRemove(conflict.ConflictId, out _); throw; }
            catch (Exception error) when (error is not OperationCanceledException)
            { _announcedDeletionConflicts.TryRemove(conflict.ConflictId, out _); }
        }
    }

    private async IAsyncEnumerable<MirrorPulseConflictRecord> ReadOriginalConflictEpochsAsync(InstanceId instanceId,
        IEnumerable<Guid> originalIds, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        const int pageSize = 64;
        foreach (Guid original in originalIds)
        {
            Guid? after = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await _catalog.ReadUploadConflictEpochsAsync(instanceId, original, after, pageSize, token).ConfigureAwait(false);
                foreach (var conflict in page) yield return conflict;
                if (page.Count < pageSize) break;
                after = page[^1].ConflictId;
            }
        }
    }

    private async ValueTask AcknowledgeDeletedOriginalsAsync(MirrorPulseConflictDeletionIntent intent, CancellationToken token)
        => await AcknowledgeRemovedOriginalsAsync(intent.ItemId, intent.OriginalOperationIds, token).ConfigureAwait(false);

    private async ValueTask AcknowledgeRemovedOriginalsAsync(Guid itemId, IReadOnlyList<Guid> originalIds, CancellationToken token)
    {
        await using (var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            foreach (Guid originalId in originalIds)
            {
                var original = await transaction.Operations.GetAsync(originalId, token).ConfigureAwait(false);
                if (original is not null && original.ItemId != itemId)
                    throw new InvalidDataException("The retained deletion cannot acknowledge a different official logical item.");
            }
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        await _feed.AcknowledgeAsync(originalIds.Select(id => new CloudLocalChangeAcknowledgement(id, null)), token).ConfigureAwait(false);
        await using var checking = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (Guid id in originalIds)
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
