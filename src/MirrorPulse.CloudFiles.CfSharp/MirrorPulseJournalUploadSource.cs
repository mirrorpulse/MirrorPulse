using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseJournalUploadBatch(
    IReadOnlyList<MirrorPulseWorkerChangeCommand> ReadyCommands,
    int DeferredCount,
    bool RequiresFullRescan)
{
    public IReadOnlyList<MirrorPulseWorkerChangeCommand>? CompleteWindow { get; init; }
    public long WindowUpperSequence { get; init; }
    public bool ScanInProgress { get; init; }
    public bool PlanningInProgress { get; init; }
    public bool AdmissionInProgress { get; init; }

    // Only the finite-window source supplies this exact immutable list after
    // validating its original sequence. A copied batch with different ready
    // commands must take the ordinary ordering path again.
    internal IReadOnlyList<MirrorPulseWorkerChangeCommand>? ValidatedSequenceCommands { get; init; }
}

/// <summary>
/// Reads upload intent from CfSharp's durable local journal. The MP catalog records only an
/// idempotency fingerprint, never a parallel operation sequence or file contents.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseJournalUploadSource
{
    private readonly CloudLocalChangeFeed _feed;
    private readonly MirrorPulseJournalWindowReader<CloudLocalChangeScan> _windowReader;
    private readonly int _planningBudget;
    private readonly int _admissionBudget;
    private MirrorPulseJournalWindowPlanner? _planner;
    private MirrorPulseJournalAdmissionPlanner? _admission;
    private HashSet<Guid> _retiredHeld = [];
    private int _reading;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly Func<InstanceId, bool> _mayDispatch;
    private readonly MirrorPulseJournalUploadCompletion? _completion;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly IMirrorPulseRetiredTransferObservationHandler? _retiredSources;
    public bool RequiresFullRescan { get; private set; }
    public void ClearFullRescanRequest()
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("A running journal source cannot be reset concurrently.");
        try { ResetPlanning(); RequiresFullRescan = false; }
        finally { Volatile.Write(ref _reading, 0); }
    }

    public MirrorPulseJournalUploadSource(
        CloudLocalChangeFeed feed,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseJournalUploadCompletion? completion = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        IMirrorPulseRetiredTransferObservationHandler? retiredSources = null,
        int scanPagesPerRead = 8,
        int planningObservationsPerRead = 2048,
        int admissionOperationsPerRead = 2048)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(planningObservationsPerRead, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planningObservationsPerRead, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(admissionOperationsPerRead, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(admissionOperationsPerRead, 4096);
        _planningBudget = planningObservationsPerRead;
        _admissionBudget = admissionOperationsPerRead;
        _feed = feed;
        _windowReader = new(feed.BeginScanAsync, scan => scan.ThroughSequence, scan => scan.RequiresFullRescan,
            async (scan, after, limit, token) =>
            {
                var page = await feed.ReadPageAsync(scan, after, limit, token).ConfigureAwait(false);
                return new(page.Changes.Select(change => new MirrorPulseLocalChangeObservation(change.OperationId,
                    change.Sequence, change.Kind, change.ItemId, change.RelativePath, change.PreviousRelativePath,
                    change.IsDirectory, change.ObservedAt)).ToArray(), page.LastScannedSequence, page.HasMore, page.RequiresFullRescan);
            }, pagesPerRead: scanPagesPerRead);
        _router = router;
        _catalog = catalog;
        _mayDispatch = mayDispatch;
        _completion = completion;
        _namespaceWork = namespaceWork;
        _retiredSources = retiredSources;
    }

    public async ValueTask<MirrorPulseJournalUploadBatch> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("The journal source has one planning owner.");
        try { return await ReadPendingCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch { ResetPlanning(); throw; }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private async ValueTask<MirrorPulseJournalUploadBatch> ReadPendingCoreAsync(CancellationToken cancellationToken)
    {
        if (_retiredSources is not null) await _retiredSources.RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
        // Preserve the feed's wait-for-notification behavior when the journal is
        // empty and recovery has finished its root scan. Otherwise continue finite
        // scans even without another journal notification. This head is only a
        // wake-up observation, never the planning window.
        if (_planner is null && _admission is null)
        {
            if (!_windowReader.HasPendingScan && _retiredSources?.RecoveryScanInProgress != true)
            {
                CloudLocalChangeBatch head = await _feed.ReadBatchAsync(cancellationToken).ConfigureAwait(false);
                if (head.RequiresFullRescan)
                {
                    _windowReader.Reset(); RequiresFullRescan = true;
                    return new([], 0, true);
                }
            }

            // A legacy batch always starts at the earliest pending row. Retaining a
            // disabled root's head must not make unrelated active roots unreachable.
            // Read the finite public window completely before planning, over bounded
            // turns so a large backlog does not monopolize one scheduling call. The
            // continuation is ephemeral; the official journal remains the authority.
            var readWindow = await _windowReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (readWindow.RequiresFullRescan)
            {
                RequiresFullRescan = true;
                return new([], 0, true);
            }

            if (readWindow.ScanInProgress)
            {
                return new([], 0, false) { ScanInProgress = true, WindowUpperSequence = readWindow.UpperSequence };
            }
            _planner = new(readWindow.Observations!, _router, readWindow.UpperSequence, _planningBudget);
            _retiredHeld = [];
        }

        // Classification can span turns. Recheck the feed's current loss marker
        // without extending the original finite window or waiting for another row.
        var currentScan = await _feed.BeginScanAsync(cancellationToken).ConfigureAwait(false);
        if (currentScan.RequiresFullRescan)
        {
            ResetPlanning(); RequiresFullRescan = true; return new([], 0, true);
        }
        HashSet<RootId> pendingRoots = (await _catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        if (_admission is null)
        {
            if (!await _planner!.AdvanceAsync((observation, token) => IncludeOriginalAsync(observation, pendingRoots, token), cancellationToken).ConfigureAwait(false))
                return new([], 0, false) { PlanningInProgress = true, WindowUpperSequence = _planner.UpperSequence };
            MirrorPulseLocalBatchPlan plan = _planner.Plan;
            long upperSequence = _planner.UpperSequence;
            _planner = null;
            if (plan.RequiresFullRescan)
            {
                ResetPlanning(); RequiresFullRescan = true; return new([], 0, true);
            }
            _admission = new(plan, upperSequence, _admissionBudget);
            pendingRoots = (await _catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        }
        if (!await _admission.AdvanceAsync((blocked, token) => RecordBlockedAsync(blocked, pendingRoots, token),
            (command, token) => PrepareCommandAsync(command, pendingRoots, token), cancellationToken).ConfigureAwait(false))
            return new([], 0, false) { AdmissionInProgress = true, WindowUpperSequence = _admission.UpperSequence };
        var result = _admission.Result;
        _admission = null; _retiredHeld = [];
        return result;
    }

    private async ValueTask<bool> RecordBlockedAsync(MirrorPulseBlockedLocalOperation blocked, HashSet<RootId> pendingRoots, CancellationToken token)
    {
        if (_retiredHeld.Contains(blocked.OperationId)) return false;
        await _catalog.SaveBlockedLocalOperationAsync(blocked, token).ConfigureAwait(false);
        if (blocked.Reason != MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange || IsNamespaceFenced(blocked.RelativePath, pendingRoots))
            return false;
        var routed = _router.ResolvePath(blocked.RelativePath);
        RootId rootId = _router.GetRegistration(routed.InstanceId, routed.RootKey).RootId;
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
        if (_namespaceWork is not null && !_namespaceWork.TryEnter([rootId], out admission)) return false;
        using (admission)
        {
            // Each original is settled only after its durable unsupported result,
            // under the same namespace admission. This grants no remote metadata acceptance.
            await _feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(blocked.OperationId, null)], token).ConfigureAwait(false);
        }
        return true;
    }

    private async ValueTask<bool> PrepareCommandAsync(MirrorPulseWorkerChangeCommand command, HashSet<RootId> pendingRoots, CancellationToken token)
    {
        RootRegistration root = _router.GetRegistration(command.InstanceId, command.RootKey);
        if (pendingRoots.Contains(root.RootId) || _retiredHeld.Contains(command.OperationId)) return false;
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
        if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out admission)) return false;
        using (admission)
        {
            byte[] fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command));
            // Official identity projection may clear a journal's old ItemId reference.
            // The operation, routing, paths, kind, sequence and observation remain fixed;
            // upload-time native object binding is retained separately in the intent.
            byte[] stableFingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command with { ItemId = null }));
            try
            {
                await _catalog.TryRecordWorkerRequestAsync(
                command.OperationId,
                command.InstanceId,
                fingerprint,
                    stableFingerprint, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                await _catalog.SaveBlockedLocalOperationAsync(new(command.OperationId, command.InstanceId,
                    command.RelativePath, MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch, command.ObservedAt), token).ConfigureAwait(false);
                return false;
            }
            await _catalog.ClearBlockedLocalOperationAsync(command.OperationId, token).ConfigureAwait(false);
            DateTimeOffset? retryAfter = _completion is null
                ? null
                : await _completion.GetRetryAfterAsync(command.OperationId, token)
                    .ConfigureAwait(false);
            return _mayDispatch(command.InstanceId) && root.State == RootRegistrationState.Active && !pendingRoots.Contains(root.RootId) &&
                !await _catalog.HasPendingUploadConflictAsync(command.OperationId, token)
                    .ConfigureAwait(false) &&
                (retryAfter is null || retryAfter <= DateTimeOffset.UtcNow);
        }
    }

    private async ValueTask<bool> IncludeOriginalAsync(MirrorPulseLocalChangeObservation observation,
        HashSet<RootId> pendingRoots, CancellationToken token)
    {
        if (observation.ItemId is not { } itemId) return true;
        var candidates = await _catalog.ReadNamespaceTransferSourceGenerationsAsync(itemId, token).ConfigureAwait(false);
        if (candidates.Count == 0) return true;
        bool accepted = candidates.Count == 1 && _retiredSources is not null &&
            !pendingRoots.Contains(candidates[0].Transfer.Plan.SourceRoute.RootId) &&
            await _retiredSources.TryAcknowledgeAsync(observation, candidates[0], token).ConfigureAwait(false);
        if (accepted) return false;
        else
        {
            _retiredHeld.Add(observation.OperationId);
            await _catalog.SaveBlockedLocalOperationAsync(new(observation.OperationId,
                candidates.Count == 1 ? candidates[0].Transfer.Plan.SourceRoute.InstanceId : null,
                observation.RelativePath, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired, observation.ObservedAt), token).ConfigureAwait(false);
        }
        return true;
    }

    private void ResetPlanning() { _windowReader.Reset(); _planner = null; _admission = null; _retiredHeld = []; }

    private bool IsNamespaceFenced(string path, HashSet<RootId> pendingRoots)
    {
        MirrorPulseRoutedItem routed = _router.ResolvePath(path);
        return pendingRoots.Contains(_router.GetRegistration(routed.InstanceId, routed.RootKey).RootId);
    }
}
