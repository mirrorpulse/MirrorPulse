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
    bool RequiresFullRescan);

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
    public bool RequiresFullRescan { get; private set; }
    public void ClearFullRescanRequest() => RequiresFullRescan = false;

    public MirrorPulseJournalUploadSource(
        CloudLocalChangeFeed feed,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseJournalUploadCompletion? completion = null)
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
    }

    public async ValueTask<MirrorPulseJournalUploadBatch> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
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

        MirrorPulseLocalBatchPlan plan = MirrorPulseLocalBatchMapper.MapObservations(observations, _router);
        if (plan.RequiresFullRescan)
        {
            RequiresFullRescan = true;
            return new([], 0, true);
        }

        var ready = new List<MirrorPulseWorkerChangeCommand>(plan.Commands.Count);
        HashSet<RootId> pendingRoots = (await _catalog.ReadManagedRootRenamesAsync(cancellationToken).ConfigureAwait(false))
            .Where(intent => intent.IsPending).Select(intent => intent.RootId).ToHashSet();
        var held = new List<MirrorPulseWorkerChangeCommand>();
        int deferred = plan.BlockedOperations?.Count ?? 0;
        foreach (MirrorPulseBlockedLocalOperation blocked in plan.BlockedOperations ?? [])
            await _catalog.SaveBlockedLocalOperationAsync(blocked, cancellationToken).ConfigureAwait(false);
        Guid[] excludedMetadata = (plan.BlockedOperations ?? [])
            .Where(operation => operation.Reason == MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange)
            .Select(operation => operation.OperationId).ToArray();
        if (excludedMetadata.Length != 0)
        {
            // Durable product results accept the notification's unsupported meaning,
            // not remote metadata. No revision is advanced. Settling these entries
            // also prevents excluded metadata from starving a bounded official batch.
            await _feed.AcknowledgeAsync(excludedMetadata.Select(operationId =>
                new CloudLocalChangeAcknowledgement(operationId, null)), cancellationToken).ConfigureAwait(false);
        }
        foreach (MirrorPulseWorkerChangeCommand command in plan.Commands.OrderBy(command => command.Sequence))
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
                    stableFingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
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
            RootRegistration root = _router.GetRegistration(command.InstanceId, command.RootKey);
            if (_mayDispatch(command.InstanceId) && root.State == RootRegistrationState.Active && !pendingRoots.Contains(root.RootId) &&
                !await _catalog.HasPendingUploadConflictAsync(command.OperationId, cancellationToken)
                    .ConfigureAwait(false) &&
                (retryAfter is null || retryAfter <= DateTimeOffset.UtcNow))
            {
                if (held.Any(earlier => MirrorPulseJournalOrderPolicy.DependsOn(command, earlier)))
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

        return new(ready.AsReadOnly(), deferred, false);
    }
}
