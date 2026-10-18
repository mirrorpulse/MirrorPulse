using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepBothCheckpoint { AfterDecisionPrepared, AfterRemotePreserved, AfterCopyRecorded, AfterRetryAuthorized, BeforeEffectCopyVerification }

public sealed partial class MirrorPulseUploadConflictActions
{
    internal Func<MirrorPulseKeepBothCheckpoint, CancellationToken, ValueTask>? KeepBothCheckpoint { get; init; }

    [SupportedOSPlatform("windows10.0.26100")]
    private async ValueTask PrepareKeepBothAdmittedAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseJournalCoalescingPlan group, RootId? admittedRoot, CancellationToken token)
    {
        RequireKeepBothService();
        var root = _router!.GetRegistration(group.InstanceId, group.RootKey);
        RequireKeepBothRoot(root, admittedRoot);
        var target = await _catalog.ReadConflictKeepBothTargetAsync(conflict.ConflictId, group.RootKey, group.ItemId,
            conflict.RelativePath, token).ConfigureAwait(false);
        if (target.InstanceId != group.InstanceId || target.RemoteRevision != conflict.RemoteRevision)
            throw new InvalidDataException("The selected conflict changed before preserving its remote version.");
        var source = await _namespaceExecution!.RunNormalUserOperationAsync(() => _remoteCopies!.CaptureSourceAsync(conflict,
            target.RootKey, target.RelativePath, token)).ConfigureAwait(false);
        var execution = await _catalog.ReadJournalCoalescingExecutionAsync(group.PlanId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepBoth execution is missing.");
        MirrorPulseCoalescingContent? content = execution.IsMoveOnly || execution.Steps.Any(step => step.Kind == MirrorPulseCoalescingStepKind.VerifyUnchanged)
            ? await CaptureGroupKeepLocalContentAsync(group, admittedRoot, token).ConfigureAwait(false) : null;
        _ = await _catalog.PrepareConflictKeepBothAsync(commandId, conflict.ConflictId, group.ItemId, conflict.RelativePath, source, content, token).ConfigureAwait(false);
        await KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint.AfterDecisionPrepared, token).ConfigureAwait(false);
    }

    internal async ValueTask<MirrorPulseConflictResolution> ResumeConflictKeepBothAsync(Guid commandId, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireKeepBothService();
        var decision = await _catalog.ReadConflictKeepBothAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original preservation decision is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RemoteSource.RootKey);
        await using var admission = _namespaceWork is null ? null : await _namespaceWork.EnterPendingOperationAsync(root.RootId, token).ConfigureAwait(false);
        return _scheduler is null ? await ExecuteKeepBothAdmittedAsync(commandId, root.RootId, token).ConfigureAwait(false) :
            await _scheduler.RunAsync(root.InstanceId, current => ExecuteKeepBothAdmittedAsync(commandId, root.RootId, current), token).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseConflictResolution> ExecuteKeepBothAdmittedAsync(Guid commandId, RootId? admittedRoot, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireKeepBothService();
        var decision = await _catalog.ReadConflictKeepBothAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original preservation decision is missing.");
        var intent = decision.Intent;
        var root = _router!.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        RequireKeepBothRoot(root, admittedRoot);
        var conflict = await _catalog.ReadUploadConflictAsync(intent.ConflictId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original preservation conflict disappeared.");
        // Even when the copy was flushed before a lost return, PreserveAsync recovers
        // that exact source-bound manifest without accessing a Worker again.
        if (!decision.RetryAuthorized)
        {
            await _namespaceExecution!.RunNormalUserOperationAsync(() => _remoteCopies!.PreserveAsync(commandId, conflict, intent.RemoteSource, token)).ConfigureAwait(false);
            await KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint.AfterRemotePreserved, token).ConfigureAwait(false);
        }
        await using var copy = await _namespaceExecution!.RunNormalUserOperationAsync(() => _copies.OpenRemoteDecisionAsync(commandId,
            conflict, intent.RemoteSource, token)).ConfigureAwait(false);
        await _catalog.RecordConflictKeepBothCopyAsync(commandId, copy.Proof, token).ConfigureAwait(false);
        await KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint.AfterCopyRecorded, token).ConfigureAwait(false);
        if (!decision.RetryAuthorized)
        {
            if (intent.AuthorizedContent is { } content)
                _ = await _catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(commandId, intent.ConflictId, content, token).ConfigureAwait(false);
            else
                _ = await _catalog.AuthorizeJournalCoalescingRetryAsync(commandId, intent.ConflictId, true, token).ConfigureAwait(false);
            await KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint.AfterRetryAuthorized, token).ConfigureAwait(false);
        }
        // Do not recreate missing originals or claim ACK. Existing pending originals
        // have their old retry delay reset together, using the public SDK repository.
        var authorized = await _catalog.ReadConflictKeepBothAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The authorized preservation decision disappeared.");
        await ResetKeepBothOriginalRetriesAsync(_state.OpenStore, authorized, token).ConfigureAwait(false);
        await _catalog.RecordConflictKeepBothOriginalRetriesPreparedAsync(commandId, token).ConfigureAwait(false);
        await foreach (var epoch in ReadOriginalConflictEpochsAsync(intent.InstanceId, intent.OriginalOperationIds, token).ConfigureAwait(false))
            if (!epoch.IsPending) _conflictCenter?.Remove(epoch.ConflictId);
        var command = await _catalog.ReadUserCommandAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The preservation command disappeared.");
        if (command.State == "failed")
            await _catalog.SaveUserCommandAsync(new(commandId, "keepboth", intent.ConflictId.ToString("D"), "pending"), token).ConfigureAwait(false);
        return new(commandId, intent.ConflictId, MirrorPulseConflictAction.KeepBoth, copy.Path, DateTimeOffset.UtcNow);
    }

    private void RequireKeepBothRoot(RootRegistration root, RootId? admittedRoot)
    {
        if (admittedRoot != root.RootId || root.State != RootRegistrationState.Active || !_mayDispatch(root.InstanceId) ||
            _namespaceWork is not null && !_namespaceWork.IsAdmitted(root.RootId))
            throw new InvalidOperationException("An unavailable root retains its preservation decision without source or local content access.");
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private void RequireKeepBothService()
    {
        if (_remoteCopies is null || _router is null || _fileSystem is null || _namespaceExecution is null)
            throw new NotSupportedException("Preserving both sides requires the protected Host and its bounded Worker content transports.");
    }

    private ValueTask KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint checkpoint, CancellationToken token) =>
        KeepBothCheckpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;

    internal async ValueTask<IAsyncDisposable> RetainKeepBothCopiesForEffectsAsync(IReadOnlyList<Guid> commandIds, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireKeepBothService();
        if (commandIds.Count is < 1 or > 2) throw new InvalidDataException("The current preservation program must retain its bounded effect copies.");
        await KeepBothCheckpointAsync(MirrorPulseKeepBothCheckpoint.BeforeEffectCopyVerification, token).ConfigureAwait(false);
        var retention = new KeepBothCopyRetention();
        try
        {
            foreach (Guid id in commandIds)
            {
                var decision = await _catalog.ReadConflictKeepBothAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The selected preservation effect lost its decision.");
                if (!decision.RetryAuthorized || decision.Copy is null)
                    throw new InvalidDataException("The selected preservation effect has no authorized complete copy.");
                var selected = await _catalog.ReadUploadConflictAsync(decision.Intent.ConflictId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The selected preservation effect lost its original conflict epoch.");
                var copy = await _namespaceExecution!.RunNormalUserOperationAsync(() => _copies.OpenRemoteDecisionAsync(id,
                    selected, decision.Intent.RemoteSource, token)).ConfigureAwait(false);
                retention.Copies.Add(copy);
                if (copy.Proof != decision.Copy)
                    throw new InvalidDataException("The complete selected copy changed before remote dispatch.");
            }
            return retention;
        }
        catch (Exception failure)
        {
            await MirrorPulseHostResourceCleanup.RunAsync([retention.DisposeAsync], failure).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class KeepBothCopyRetention : IAsyncDisposable
    {
        internal List<MirrorPulseConflictRemoteCopyLease> Copies { get; } = [];
        public ValueTask DisposeAsync() => new(MirrorPulseHostResourceCleanup.RunAsync(Copies.Select(copy => new Func<ValueTask>(copy.DisposeAsync)).ToArray()));
    }

    internal static async ValueTask ResetKeepBothOriginalRetriesAsync(ICloudStateStore store,
        MirrorPulseConflictKeepBothDecision decision, CancellationToken token)
    {
        if (!decision.RetryAuthorized || decision.Copy is null)
            throw new InvalidOperationException("Original retry metadata requires the complete preservation authorization.");
        var intent = decision.Intent;
        await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (Guid originalId in intent.OriginalOperationIds)
        {
            var entry = await transaction.Operations.GetAsync(originalId, token).ConfigureAwait(false);
            if (entry is null) continue;
            if (entry.ItemId is { } itemId && itemId != intent.ItemId)
                throw new InvalidDataException("A pending preservation original changed its logical item.");
            await transaction.Operations.UpdateAsync(new CloudOperationJournalEntry(entry.OperationId, entry.Kind, entry.ItemId,
                entry.Payload.Span, entry.CreatedAt, 0, null, entry.Sequence), token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
