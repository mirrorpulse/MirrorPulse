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
    private MirrorPulseJournalWindowPlanner? _planner;
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
        int planningObservationsPerRead = 2048)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(planningObservationsPerRead, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planningObservationsPerRead, 4096);
        _planningBudget = planningObservationsPerRead;
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
        // empty. This head is only a wake-up observation, never the planning window.
        if (_planner is null)
        {
            if (!_windowReader.HasPendingScan)
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
        if (!await _planner.AdvanceAsync((observation, token) => IncludeOriginalAsync(observation, pendingRoots, token), cancellationToken).ConfigureAwait(false))
            return new([], 0, false) { PlanningInProgress = true, WindowUpperSequence = _planner.UpperSequence };
        MirrorPulseLocalBatchPlan plan = _planner.Plan;
        long upperSequence = _planner.UpperSequence;
        var retiredHeld = _retiredHeld;
        _planner = null; _retiredHeld = [];
        pendingRoots = (await _catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        if (plan.RequiresFullRescan)
        {
            RequiresFullRescan = true;
            return new([], 0, true);
        }

        var ready = new List<MirrorPulseWorkerChangeCommand>(plan.Commands.Count);
        var held = new MirrorPulseJournalOrderIndex();
        int deferred = plan.BlockedOperations?.Count ?? 0;
        foreach (MirrorPulseBlockedLocalOperation blocked in plan.BlockedOperations ?? [])
            if (!retiredHeld.Contains(blocked.OperationId)) await _catalog.SaveBlockedLocalOperationAsync(blocked, cancellationToken).ConfigureAwait(false);
        var excludedMetadata = (plan.BlockedOperations ?? [])
            .Where(operation => operation.Reason == MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange &&
                !retiredHeld.Contains(operation.OperationId) &&
                !IsNamespaceFenced(operation.RelativePath, pendingRoots))
            .GroupBy(operation =>
            {
                var routed = _router.ResolvePath(operation.RelativePath);
                return _router.GetRegistration(routed.InstanceId, routed.RootKey).RootId;
            });
        var acceptedMetadata = new HashSet<Guid>();
        foreach (var rootMetadata in excludedMetadata)
        {
            MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
            if (_namespaceWork is not null && !_namespaceWork.TryEnter([rootMetadata.Key], out admission)) continue;
            using var retainedAdmission = admission;
            // Retain even unsupported metadata while the root's namespace is unresolved.
            // Durable product results accept the notification's unsupported meaning,
            // not remote metadata. No revision is advanced. Settling these entries
            // also prevents excluded metadata from starving a bounded official batch.
            await _feed.AcknowledgeAsync(rootMetadata.Select(operation =>
                new CloudLocalChangeAcknowledgement(operation.OperationId, null)), cancellationToken).ConfigureAwait(false);
            acceptedMetadata.UnionWith(rootMetadata.Select(operation => operation.OperationId));
        }
        foreach (var command in plan.RoutedBlockedCommands)
            if (!acceptedMetadata.Contains(command.OperationId)) held.Add(command);
        foreach (MirrorPulseWorkerChangeCommand command in plan.Commands.OrderBy(command => command.Sequence))
        {
            RootRegistration root = _router.GetRegistration(command.InstanceId, command.RootKey);
            // A durable namespace owner must get the original notification before any ordinary
            // Worker/coalescing claim, including callers without an in-memory work admission.
            if (pendingRoots.Contains(root.RootId) || retiredHeld.Contains(command.OperationId))
            {
                held.Add(command); deferred++; continue;
            }
            MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
            if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out admission))
            {
                held.Add(command); deferred++; continue;
            }
            using var retainedAdmission = admission;
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
                    stableFingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                await _catalog.SaveBlockedLocalOperationAsync(new(command.OperationId, command.InstanceId,
                    command.RelativePath, MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch, command.ObservedAt), cancellationToken).ConfigureAwait(false);
                deferred++;
                held.Add(command);
                continue;
            }
            await _catalog.ClearBlockedLocalOperationAsync(command.OperationId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset? retryAfter = _completion is null
                ? null
                : await _completion.GetRetryAfterAsync(command.OperationId, cancellationToken)
                    .ConfigureAwait(false);
            if (_mayDispatch(command.InstanceId) && root.State == RootRegistrationState.Active && !pendingRoots.Contains(root.RootId) &&
                !await _catalog.HasPendingUploadConflictAsync(command.OperationId, cancellationToken)
                    .ConfigureAwait(false) &&
                (retryAfter is null || retryAfter <= DateTimeOffset.UtcNow))
            {
                if (held.HasEarlierDependency(command))
                {
                    held.Add(command);
                    deferred++;
                }
                else ready.Add(command);
            }
            else
            {
                held.Add(command);
                deferred++;
            }
        }

        return new(ready.AsReadOnly(), deferred, false)
        {
            CompleteWindow = plan.CompleteWindow is { } window ?
                Array.AsReadOnly(window.Where(command => !acceptedMetadata.Contains(command.OperationId)).ToArray()) : null,
            WindowUpperSequence = upperSequence,
        };
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

    private void ResetPlanning() { _windowReader.Reset(); _planner = null; _retiredHeld = []; }

    private bool IsNamespaceFenced(string path, HashSet<RootId> pendingRoots)
    {
        MirrorPulseRoutedItem routed = _router.ResolvePath(path);
        return pendingRoots.Contains(_router.GetRegistration(routed.InstanceId, routed.RootKey).RootId);
    }
}
