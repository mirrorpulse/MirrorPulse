#if HOST_ATOMIC_DIRECTORY_CANDIDATE
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class MirrorPulseJournalUploadPump
{
    private Guid? _afterAtomicDirectoryJournal;

    private async ValueTask<bool?> TryDispatchAtomicDirectoryAsync(MirrorPulseWorkerChangeCommand command, CancellationToken token)
    {
        if (command.Kind != MirrorPulseWorkerChangeKind.Delete || _mutations is not AdapterInstanceProcessSupervisor worker)
            return null;
        var retained = await _catalog.ReadAtomicDirectoryBatchForOperationAsync(command.OperationId, token).ConfigureAwait(false);
        if (retained is not null)
        {
            // The dedicated recovery pass rechecks every retained original and
            // handles both receipt recovery and journal acknowledgement.
            return false;
        }
        if (_namespaceWork is null || !worker.SupportsAtomicDirectoryDeletion(command.InstanceId)) return null;
        if (_planningBatch?.CompleteWindow is not { } window) return false;

        var members = SelectAtomicDirectoryOriginals(command, window, out bool directoryDelete);
        if (!directoryDelete) return null;
        var ready = _planningBatch.ReadyCommands.Select(value => value.OperationId).ToHashSet();
        if (members is null || members.Any(value => !ready.Contains(value.OperationId))) return false;
        if (members.MinBy(value => value.Sequence)?.OperationId != command.OperationId) return false;
        await RequireFreshAtomicDirectoryOriginalsAsync(members, token).ConfigureAwait(false);

        var operations = new List<AdapterOriginalDirectoryOperation>(members.Count);
        var intents = new List<MirrorPulseMutationIntent>(members.Count);
        var revisions = await ReadExactAtomicDirectoryRevisionsAsync(members, token).ConfigureAwait(false);
        for (int ordinal = 0; ordinal < members.Count; ordinal++)
        {
            var member = members[ordinal];
            var retainedMutation = await _catalog.ReadMutationAsync(member.OperationId, token).ConfigureAwait(false);
            if (retainedMutation is not null && (retainedMutation.State != MirrorPulseMutationState.Prepared ||
                retainedMutation.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted))
                return false;
            string? revision = revisions[ordinal];
            if (string.IsNullOrEmpty(revision)) return false;
            var intent = Intent(member, revision) with { JournalItemId = member.ItemId };
            if (retainedMutation is not null && retainedMutation.Intent != intent) return false;
            intents.Add(intent);
            operations.Add(new("Delete", new(member.OperationId, member.RootKey, member.RelativePath.Replace('\\', '/'),
                Preconditions: new(revision), IsDirectory: member.IsDirectory)));
        }

        // The original parent Delete ID makes admission replay stable across a
        // Host stop between the read-only Worker admission and its reservation.
        var request = new AdapterDirectoryAdmissionRequest(1, command.InstanceId.Value, members[^1].OperationId, operations, string.Empty);
        request = request with { RequestFingerprint = request.CalculateFingerprint() };
        foreach (var intent in intents) await _catalog.PrepareMutationAsync(intent, token).ConfigureAwait(false);
        await worker.ExecuteAtomicDirectoryDeletionAsync(request, token, members).ConfigureAwait(false);
        await AcknowledgeAtomicDirectoryAsync(request.AdmissionId, token).ConfigureAwait(false);
        return true;
    }

    internal static IReadOnlyList<MirrorPulseWorkerChangeCommand>? SelectAtomicDirectoryOriginals(
        MirrorPulseWorkerChangeCommand trigger, IReadOnlyList<MirrorPulseWorkerChangeCommand> window, out bool directoryDelete)
    {
        string triggerPath = Portable(trigger.RelativePath);
        var root = window.Where(value => value.InstanceId == trigger.InstanceId && value.RootKey == trigger.RootKey &&
            value.Kind == MirrorPulseWorkerChangeKind.Delete && value.IsDirectory && Within(Portable(value.RelativePath), triggerPath))
            .MinBy(value => Portable(value.RelativePath).Length);
        directoryDelete = root is not null;
        if (root is null) return null;
        string subtree = Portable(root.RelativePath);
        if (subtree.Length == 0) return null;
        var originals = new List<MirrorPulseWorkerChangeCommand>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<Guid>();
        foreach (var value in window)
        {
            string path = Portable(value.RelativePath);
            bool sameRoot = value.InstanceId == root.InstanceId && value.RootKey == root.RootKey;
            bool source = sameRoot && Within(subtree, path);
            bool parent = sameRoot && value.IsDirectory && Within(path, subtree);
            bool previous = (value.PreviousInstanceId ?? value.InstanceId) == root.InstanceId &&
                (value.PreviousRootKey ?? value.RootKey) == root.RootKey && value.PreviousRelativePath is { } prior &&
                (Within(subtree, Portable(prior)) || value.IsDirectory && Within(Portable(prior), subtree));
            if (!source && !parent && !previous) continue;
            if (!source || value.Kind != MirrorPulseWorkerChangeKind.Delete || value.ItemId is not { } item || item == Guid.Empty ||
                value.PreviousRelativePath is not null || value.PreviousRootKey is not null || value.PreviousInstanceId is not null ||
                value.OperationId == Guid.Empty || value.Sequence <= 0 || !paths.Add(path) || !objects.Add(item) || originals.Count == 2048) return null;
            originals.Add(value);
        }
        if (originals.All(value => value.OperationId != trigger.OperationId)) return null;
        var directories = originals.Where(value => value.IsDirectory).Select(value => Portable(value.RelativePath)).ToHashSet(StringComparer.Ordinal);
        foreach (var value in originals.Where(value => value.OperationId != root.OperationId))
        {
            string path = Portable(value.RelativePath);
            int slash = path.LastIndexOf('/');
            if (slash < 0 || !directories.Contains(path[..slash])) return null;
        }
        return originals.OrderByDescending(value => Portable(value.RelativePath).Count(character => character == '/'))
            .ThenBy(value => value.Sequence).ToArray();
    }

    private async ValueTask<string?[]> ReadExactAtomicDirectoryRevisionsAsync(IReadOnlyList<MirrorPulseWorkerChangeCommand> members,
        CancellationToken token)
    {
        var revisions = new string?[members.Count];
        await using var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        for (int ordinal = 0; ordinal < members.Count; ordinal++)
        {
            var member = members[ordinal];
            string local = _router.ResolveUploadPath(member.InstanceId, member.RootKey, member.RelativePath);
            string path = Portable(Path.GetRelativePath(_syncRootPath, local));
            var entry = await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false);
            if (entry is null || entry.Kind != CloudStateOperationKind.Delete || entry.ItemId != member.ItemId || entry.Sequence != member.Sequence)
                throw new InvalidDataException("The original journal Delete changed before Worker admission.");
            var item = await transaction.Items.GetByItemIdAsync(member.ItemId!.Value, token).ConfigureAwait(false);
            if (item is null || !item.IsTombstone || Portable(item.RelativePath) != path ||
                item.Kind != (member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File))
                throw new InvalidDataException("The original journal Delete lost its acknowledged item identity.");
            revisions[ordinal] = item.RemoteRevision;
        }
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return revisions;
    }

    private async ValueTask RequireFreshAtomicDirectoryOriginalsAsync(IReadOnlyList<MirrorPulseWorkerChangeCommand> members, CancellationToken token)
    {
        var scan = await _feed.BeginScanAsync(token).ConfigureAwait(false);
        if (scan.RequiresFullRescan) throw new InvalidOperationException("The official journal requires reconciliation before atomic directory admission.");
        if (scan.ThroughSequence < members.Max(value => value.Sequence))
            throw new InvalidDataException("The official journal lost an original sequence.");
        var observations = new List<MirrorPulseLocalChangeObservation>();
        long after = 0;
        do
        {
            var page = await _feed.ReadPageAsync(scan, after, 128, token).ConfigureAwait(false);
            if (page.RequiresFullRescan || page.LastScannedSequence < after || page.LastScannedSequence > scan.ThroughSequence ||
                page.HasMore && page.LastScannedSequence == after)
                throw new InvalidOperationException("The official journal could not retain a complete current window.");
            long previous = after;
            foreach (var change in page.Changes)
            {
                if (change.Sequence <= previous || change.Sequence > page.LastScannedSequence)
                    throw new InvalidDataException("The official journal returned a sequence outside its current page.");
                previous = change.Sequence;
                observations.Add(new(change.OperationId, change.Sequence, change.Kind, change.ItemId, change.RelativePath,
                    change.PreviousRelativePath, change.IsDirectory, change.ObservedAt));
                if (observations.Count > 16384)
                    throw new InvalidOperationException("The current journal window exceeds the atomic directory admission bound.");
            }
            if (!page.HasMore) break;
            after = page.LastScannedSequence;
        } while (true);
        var window = MirrorPulseLocalBatchMapper.MapObservations(observations, _router).CompleteWindow;
        if (window is null ||
            SelectAtomicDirectoryOriginals(members[^1], window, out _) is not { } fresh || !members.SequenceEqual(fresh))
            throw new InvalidOperationException("The complete current journal window changed before atomic directory admission.");
    }

    private async ValueTask AcknowledgeAtomicDirectoryAsync(Guid admissionId, CancellationToken token)
    {
        var acknowledgement = new MirrorPulseAtomicDirectoryJournalAcknowledgement(_catalog, _state.OpenStore,
            (ids, stop) => _feed.AcknowledgeAsync(ids, stop));
        foreach (Guid id in await acknowledgement.AcknowledgeAsync(admissionId, token).ConfigureAwait(false))
        {
            _completedCoalescedOriginals.Add(id);
            _runner.ClearRecoveredFault(id);
        }
    }

    private async ValueTask<bool> RecoverAtomicDirectoryJournalAsync(CancellationToken token)
    {
        if (_mutations is not AdapterInstanceProcessSupervisor worker) return false;
        const int pageSize = 4;
        var ids = await _catalog.ReadPendingAtomicDirectoryJournalAsync(_afterAtomicDirectoryJournal, pageSize, token).ConfigureAwait(false);
        bool failed = false;
        foreach (Guid id in ids)
        {
            try
            {
                var batch = await _catalog.ReadAtomicDirectoryBatchAsync(id, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original atomic journal lost its retained Worker request.");
                if (batch.Receipt is null && !await RecoverAtomicDirectoryReceiptAsync(worker, batch, token).ConfigureAwait(false))
                {
                    failed = true;
                    continue;
                }
                await AcknowledgeAtomicDirectoryAsync(id, token).ConfigureAwait(false);
                _runner.ClearAtomicDirectoryRecoveryFault(id);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                failed = true;
                _runner.RetainAtomicDirectoryRecoveryFault(id);
                await ReportFailureAsync(null, "AtomicDirectoryJournalAcknowledgementFailed", error, token).ConfigureAwait(false);
            }
        }
        _afterAtomicDirectoryJournal = ids.Count == pageSize ? ids[^1] : null;
        return failed || ids.Count == pageSize;
    }

    private async ValueTask<bool> RecoverAtomicDirectoryReceiptAsync(AdapterInstanceProcessSupervisor worker,
        MirrorPulseAtomicDirectoryBatch batch, CancellationToken token)
    {
        var request = batch.Request.OriginalRequest;
        var journal = await _catalog.ReadAtomicDirectoryJournalAsync(request.AdmissionId, token).ConfigureAwait(false);
        var parent = journal.Originals[^1];
        if (_namespaceWork is null || !worker.SupportsAtomicDirectoryDeletion(parent.InstanceId) ||
            !_router.Registrations.Any(root => root.InstanceId == parent.InstanceId && root.UniquenessKey == parent.RootKey)) return false;
        var rootId = _router.GetRegistration(parent.InstanceId, parent.RootKey).RootId;
        if (!_namespaceWork.TryEnter([rootId], out var lease)) return false;
        using (lease)
        {
            return await _scheduler.RunAsync(parent.InstanceId, async stop =>
            {
                foreach (var original in journal.Originals)
                    if (!await MirrorPulseJournalDispatchReadiness.MayDispatchAsync(original, _router, _catalog, _mayDispatch, stop)
                        .ConfigureAwait(false)) return false;
                var acknowledgement = new MirrorPulseAtomicDirectoryJournalAcknowledgement(_catalog, _state.OpenStore,
                    (ids, cancellation) => _feed.AcknowledgeAsync(ids, cancellation));
                if (await acknowledgement.CountPendingOriginalsAsync(journal, stop).ConfigureAwait(false) != journal.Originals.Count)
                    throw new InvalidDataException("The pending atomic directory request lost its official originals.");
                await worker.ExecuteAtomicDirectoryDeletionAsync(request, stop).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        }
    }

    private static string Portable(string path) => path.Replace('\\', '/');

    private static bool Within(string root, string path) =>
        root.Length == 0 || path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
#endif
