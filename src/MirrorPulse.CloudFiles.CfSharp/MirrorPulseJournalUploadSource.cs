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
}

/// <summary>
/// Reads upload intent from CfSharp's durable local journal. The MP catalog records only an
/// idempotency fingerprint, never a parallel operation sequence or file contents.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseJournalUploadSource
{
    private const int ScanPageSize = 64;
    private readonly CloudLocalChangeFeed _feed;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly Func<InstanceId, bool> _mayDispatch;
    private readonly MirrorPulseJournalUploadCompletion? _completion;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly IMirrorPulseRetiredTransferObservationHandler? _retiredSources;
    public bool RequiresFullRescan { get; private set; }
    public void ClearFullRescanRequest() => RequiresFullRescan = false;

    public MirrorPulseJournalUploadSource(
        CloudLocalChangeFeed feed,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseJournalUploadCompletion? completion = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        IMirrorPulseRetiredTransferObservationHandler? retiredSources = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        _feed = feed;
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
        if (_retiredSources is not null) await _retiredSources.RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
        // Preserve the feed's wait-for-notification behavior when the journal is
        // empty. This head is only a wake-up observation, never the planning window.
        CloudLocalChangeBatch head = await _feed.ReadBatchAsync(cancellationToken).ConfigureAwait(false);
        if (head.RequiresFullRescan)
        {
            RequiresFullRescan = true;
            return new([], 0, true);
        }

        // A legacy batch always starts at the earliest pending row. Retaining a
        // disabled root's head must not make unrelated active roots unreachable.
        // Read the finite public scan completely before planning so dependencies
        // on earlier pages survive page boundaries. No cursor is persisted, and
        // scanning never acknowledges or replaces an official journal record.
        CloudLocalChangeScan scan = await _feed.BeginScanAsync(cancellationToken).ConfigureAwait(false);
        if (scan.RequiresFullRescan)
        {
            RequiresFullRescan = true;
            return new([], 0, true);
        }

        var observations = new List<MirrorPulseLocalChangeObservation>();
        long afterSequence = 0;
        while (true)
        {
            CloudLocalChangePage page = await _feed.ReadPageAsync(scan, afterSequence, ScanPageSize,
                cancellationToken).ConfigureAwait(false);
            if (page.RequiresFullRescan)
            {
                RequiresFullRescan = true;
                return new([], 0, true);
            }

            foreach (CloudLocalChange change in page.Changes)
                observations.Add(new(change.OperationId, change.Sequence, change.Kind, change.ItemId,
                    change.RelativePath, change.PreviousRelativePath, change.IsDirectory, change.ObservedAt));
            if (!page.HasMore) break;
            if (page.LastScannedSequence <= afterSequence || page.LastScannedSequence > scan.ThroughSequence)
                throw new InvalidDataException("The public journal scan did not advance inside its captured boundary.");
            afterSequence = page.LastScannedSequence;
        }

        HashSet<RootId> pendingRoots = (await _catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var retiredHeld = await SettleRetiredSourcesAsync(observations, pendingRoots, cancellationToken).ConfigureAwait(false);
        MirrorPulseLocalBatchPlan plan = MirrorPulseLocalBatchMapper.MapObservations(observations, _router);
        if (plan.RequiresFullRescan)
        {
            RequiresFullRescan = true;
            return new([], 0, true);
        }

        var ready = new List<MirrorPulseWorkerChangeCommand>(plan.Commands.Count);
        var held = new List<MirrorPulseWorkerChangeCommand>();
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
        held.AddRange(plan.RoutedBlockedCommands.Where(command => !acceptedMetadata.Contains(command.OperationId)));
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
                if (held.Any(earlier => earlier.Sequence < command.Sequence && MirrorPulseJournalOrderPolicy.DependsOn(command, earlier)))
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
            WindowUpperSequence = scan.ThroughSequence,
        };
    }

    private async Task<HashSet<Guid>> SettleRetiredSourcesAsync(List<MirrorPulseLocalChangeObservation> observations,
        HashSet<RootId> pendingRoots, CancellationToken token)
    {
        var held = new HashSet<Guid>(); var settled = new HashSet<Guid>();
        foreach (var group in observations.Where(value => value.ItemId is not null).GroupBy(value => value.ItemId!.Value))
        {
            var candidates = await _catalog.ReadNamespaceTransferSourceGenerationsAsync(group.Key, token).ConfigureAwait(false);
            if (candidates.Count == 0) continue;
            foreach (var observation in group)
            {
                bool accepted = candidates.Count == 1 && _retiredSources is not null &&
                    !pendingRoots.Contains(candidates[0].Transfer.Plan.SourceRoute.RootId) &&
                    await _retiredSources.TryAcknowledgeAsync(observation, candidates[0], token).ConfigureAwait(false);
                if (accepted) settled.Add(observation.OperationId);
                else
                {
                    held.Add(observation.OperationId);
                    await _catalog.SaveBlockedLocalOperationAsync(new(observation.OperationId,
                        candidates.Count == 1 ? candidates[0].Transfer.Plan.SourceRoute.InstanceId : null,
                        observation.RelativePath, MirrorPulseLocalOperationBlockReason.RootReconciliationRequired, observation.ObservedAt), token).ConfigureAwait(false);
                }
            }
        }
        observations.RemoveAll(value => settled.Contains(value.OperationId));
        return held;
    }

    private bool IsNamespaceFenced(string path, HashSet<RootId> pendingRoots)
    {
        MirrorPulseRoutedItem routed = _router.ResolvePath(path);
        return pendingRoots.Contains(_router.GetRegistration(routed.InstanceId, routed.RootKey).RootId);
    }
}
