using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Yields after one durable product page without changing the actual SDK result or Adapter cursor.</summary>
internal sealed class MirrorPulseRemoteTreeSettlementPendingException(Guid operationId, int remainingMembers)
    : IOException("The original SDK tree has a further bounded member settlement page.")
{
    internal Guid OperationId { get; } = operationId;
    internal int RemainingMembers { get; } = remainingMembers;
}

/// <summary>Applies one unchanged SDK directory Delete, then settles its original registered tree.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteTreeRetirementCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteRetirementCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    private readonly MirrorPulseRemoteRetirementBatchCoordinator _facts = new(fileSystem, state, catalog, router, execution, actor);

    internal async ValueTask<CloudRemoteApplyResult> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        var original = await catalog.ReadNamespaceRemoteRetirementTreeAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        Guid memberId = original?.MemberId ?? (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
            .InspectAsync(change.RelativePath, true, token).ConfigureAwait(false)).MemberId;
        if (original is not null) RequireOriginal(original, instance, batch, ordinal, options);
        return await actor.RunAsync(async admitted =>
        {
            var child = original?.RemoteEntry?.Child ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original directory delete has no protected membership.");
            var parent = original?.RemoteEntry?.Parent ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(
                child.Context.Location.ParentMemberId ?? throw new InvalidDataException("A remote tree cannot delete the sync-root anchor."), admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original directory delete has no direct parent.");
            _facts.RequireCurrentRoute(instance, change, child, parent);
            if (original is not null && !await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another owner or terminal state prevents original directory replay.");
            CloudRemoteApplyResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var parentItem = fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parentItem, router, admitted).ConfigureAwait(false);
                var before = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(parent, parentItem, guard, admitted).ConfigureAwait(false);
                if (original is null)
                {
                    var captured = await CaptureOriginalAsync(memberId, admitted).ConfigureAwait(false);
                    var remote = MirrorPulseRemoteRetirementBatchCoordinator.Reference(instance, batch, ordinal, options,
                        child, parent, captured[0].NativeRemoteRevision, captured[0].OfficialRemoteRevision);
                    original = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(Guid.NewGuid(), remote, captured,
                        DateTimeOffset.UtcNow, admitted).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
                }
                var start = await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId, admitted).ConfigureAwait(false);
                await InspectOriginalInventoryAsync(original, start is not null, admitted).ConfigureAwait(false);
                await _facts.RequirePrefixAsync(batch, ordinal, false, admitted).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(admitted).ConfigureAwait(false));
                if (start is null) await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, DateTimeOffset.UtcNow, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterStart, admitted).ConfigureAwait(false);
                result = await fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply, CancellationToken.None).ConfigureAwait(false);
                if (result.AppliedEntryCount <= ordinal) return;
                if (result.AppliedEntryCount != ordinal + 1) throw new InvalidDataException("The SDK crossed more than its original directory entry.");
                await SettleUnderParentAsync(original, instance, batch, ordinal, result, conflicts, parentItem, guard, before, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token).ConfigureAwait(false);
    }

    internal async ValueTask SettleRecordedAsync(MirrorPulseNamespaceRetirementBatch original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        RequireOriginal(original, instance, batch, ordinal, options);
        await actor.RunAsync(async admitted =>
        {
            original = await catalog.ReadNamespaceRetirementBatchAsync(original.OperationId, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The consumed SDK directory lost its original tree admission.");
            if (original.Phase == MirrorPulseNamespaceRetirementBatchPhase.Completed) return true;
            if (!await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents original tree settlement.");
            var remote = original.RemoteEntry!; _facts.RequireCurrentRoute(instance, batch.Changes[ordinal], remote.Child, remote.Parent);
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var parent = fileSystem.GetDirectory(remote.Parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, admitted).ConfigureAwait(false);
                var before = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(remote.Parent, parent, guard, admitted).ConfigureAwait(false);
                await SettleUnderParentAsync(original, instance, batch, ordinal, null, conflicts, parent, guard, before, admitted).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeEntry[]> CaptureOriginalAsync(Guid member, CancellationToken token)
    {
        var graph = await catalog.ReadNamespaceMemberTreeAsync(member, token).ConfigureAwait(false);
        var entries = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>(graph.Count);
        foreach (var node in graph)
        {
            var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(node.MemberId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original tree member lost its protection and identity.");
            CloudItem item = Item(original); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            if (!snapshot.Exists) throw new InvalidDataException("A missing original member cannot invent a new directory admission.");
            _ = await _facts.InspectChildAsync(original, item, null, token, requireRevision: false).ConfigureAwait(false);
            var projection = await RequireMemberProjectionAsync(original, token).ConfigureAwait(false);
            if (projection.IsTombstone) throw new InvalidDataException("An existing original tree member already has a public SDK tombstone.");
            entries.Add(new(entries.Count, node.Depth, original, CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).RemoteRevision,
                projection.RemoteRevision));
        }
        return entries.ToArray();
    }

    private async Task InspectOriginalInventoryAsync(MirrorPulseNamespaceRetirementBatch batch, bool started, CancellationToken token)
    {
        await RequireOriginalCurrentMembershipAsync(catalog, batch, token).ConfigureAwait(false);
        var allowed = batch.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var root = fileSystem.GetDirectory(batch.RemoteEntry!.Child.Context.Location.RelativePath);
        var rootSnapshot = await root.InspectAsync(token).ConfigureAwait(false);
        if (rootSnapshot.Exists)
        {
            _ = await _facts.InspectChildAsync(batch.RemoteEntry.Child, root, batch.RemoteEntry.NativeRemoteRevision, token).ConfigureAwait(false);
            var directories = new Queue<CloudDirectory>(); directories.Enqueue(root); int count = 1;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.RelativePath.Replace('\\', '/') };
            while (directories.TryDequeue(out var directory))
                await foreach (var item in directory.EnumerateLocalChildrenAsync(cancellationToken: token).ConfigureAwait(false))
                {
                    string path = item.RelativePath.Replace('\\', '/');
                    if (++count > 65_536 || !seen.Add(path) || !allowed.TryGetValue(path, out var retained))
                        throw new InvalidDataException("An unknown native descendant cannot join the original SDK tree inventory.");
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
                    var facts = await lease.InspectAsync(token).ConfigureAwait(false);
                    if (facts.LocalObject != retained.LocalObject || facts.IsDirectory != retained.IsDirectory || facts.OwnerSid != retained.OwnerSid ||
                        facts.RootId != batch.RootId || facts.RelativePath != retained.RelativePath || facts.LinkCount != 1)
                        throw new InvalidDataException("An original native descendant changed its binding, position, kind or owner.");
                    if (item is CloudDirectory nested) directories.Enqueue(nested);
                }
        }
        else RequireActuallyMissing(root);
        for (int after = -1; after + 1 < batch.Entries.Count;)
        {
            var page = await catalog.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId, after, 64, token).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                var item = Item(entry.Original); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
                var projection = await RequireMemberProjectionAsync(entry.Original, token).ConfigureAwait(false);
                if (snapshot.Exists)
                {
                    _ = await _facts.InspectChildAsync(entry.Original, item, entry.NativeRemoteRevision, token).ConfigureAwait(false);
                    if (projection.IsTombstone || projection.RemoteRevision != entry.OfficialRemoteRevision)
                        throw new InvalidDataException("A remaining original directory member lost its retained SDK revision.");
                }
                else
                {
                    RequireActuallyMissing(item);
                    string? tombstoneRevision = entry.Ordinal == 0 ? batch.RemoteEntry.RemoteRevision : entry.OfficialRemoteRevision;
                    if (!started || projection.RemoteRevision != (projection.IsTombstone ? tombstoneRevision : entry.OfficialRemoteRevision))
                        throw new InvalidDataException("A missing original tree member requires its original start and supported SDK recovery projection.");
                }
            }
            after = page.LastOrdinal;
        }
    }

    internal static async Task RequireOriginalCurrentMembershipAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceRetirementBatch batch, CancellationToken token)
    {
        for (int after = -1; after + 1 < batch.Entries.Count;)
        {
            var page = await catalog.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId, after, 64, token).ConfigureAwait(false);
            foreach (var entry in page.Entries)
                if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.Original.Context.Member.MemberId, token).ConfigureAwait(false) != entry.Original)
                    throw new InvalidDataException("The original SDK tree lost current member names, positions, identities or protection before invocation.");
            after = page.LastOrdinal;
        }
    }

    private async Task SettleUnderParentAsync(MirrorPulseNamespaceRetirementBatch original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyResult? result, MirrorPulseRemoteConflictProjector? conflicts,
        CloudDirectory parent, MirrorPulseWindowsNamespacePermissionLease guard, MirrorPulseNamespacePermissionObject before, CancellationToken token)
    {
        var remote = original.RemoteEntry!;
        var start = await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The consumed SDK directory lost its original invocation start.");
        await _facts.RequirePrefixAsync(batch, ordinal, true, token).ConfigureAwait(false);
        var outcome = await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(original.OperationId, token).ConfigureAwait(false);
        if (outcome is null)
        {
            var records = await execution.RunNormalUserOperationAsync(async () => await fileSystem.ListRemoteConflictsAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            var matches = records.Where(record => record.Conflict.DetectedAt >= start.StartedAt &&
                MirrorPulseRemoteRetirementBatchCoordinator.Matches(batch, ordinal, record.Conflict.Change)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("The original directory has ambiguous durable SDK conflicts.");
            string status = matches.Length == 0 ? "CommittedPrefix" : "Conflict";
            if (result is not null)
            {
                var entry = result.Entries.SingleOrDefault(value => value.ChangeId == remote.ChangeId);
                if (entry is null || entry.Status is not (CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied or CloudRemoteApplyEntryStatus.Conflict) ||
                    (entry.Status == CloudRemoteApplyEntryStatus.Conflict) != (matches.Length == 1) || result.ConflictIds.Count != matches.Length ||
                    matches.Length == 1 && result.ConflictIds[0] != matches[0].ConflictId)
                    throw new InvalidDataException("The actual SDK directory result disagrees with its durable original outcome.");
                status = entry.Status.ToString();
            }
            var rootItem = Item(remote.Child); var snapshot = await rootItem.InspectAsync(token).ConfigureAwait(false);
            var projection = await RequireMemberProjectionAsync(remote.Child, token).ConfigureAwait(false);
            MirrorPulseNamespacePermissionVerification? child = null;
            if (snapshot.Exists) child = await _facts.InspectChildAsync(remote.Child, rootItem, remote.NativeRemoteRevision, token).ConfigureAwait(false);
            else RequireActuallyMissing(rootItem);
            var after = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(remote.Parent, parent, guard, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, after);
            await using (var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                var prefix = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original directory lost its public SDK prefix.");
                outcome = new(1, original.OperationId, remote.BatchFingerprint, original.RemoteTreeFingerprint!, start.StartedAt,
                    prefix.AppliedEntryCount, prefix.LastAppliedChangeId!, Convert.ToHexString(SHA256.HashData(prefix.Cursor.Span)),
                    prefix.Status == CloudRemoteBatchStatus.Applied, status, matches.Length == 0 ? null : matches[0].ConflictId,
                    matches.Length == 0 ? null : matches[0].Conflict.DetectedAt, matches.Length == 0 ? null : matches[0].Conflict.Reason.ToString(),
                    DateTimeOffset.UtcNow, !snapshot.Exists, projection.IsTombstone, projection.RemoteRevision,
                    MirrorPulseRemoteRetirementBatchCoordinator.Verification(after), child);
                await transaction.RollbackAsync(token).ConfigureAwait(false);
            }
            if (matches.Length == 1 && conflicts is not null) await conflicts.CaptureAsync(instance, matches[0].ConflictId, matches[0].Conflict, token).ConfigureAwait(false);
            await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome, token).ConfigureAwait(false);
            if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterTreeOutcome, token).ConfigureAwait(false);
        }
        var progress = (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(original.OperationId, token).ConfigureAwait(false))!;
        if (progress.NextOrdinal >= 0)
        {
            int first = Math.Max(0, progress.NextOrdinal - 63);
            var page = await catalog.ReadNamespaceRemoteRetirementTreePageAsync(original.OperationId, first - 1, progress.NextOrdinal - first + 1, token).ConfigureAwait(false);
            var observed = new List<(MirrorPulseNamespaceRemoteRetirementTreeEntry Original, bool Missing, CloudItemState Projection, MirrorPulseNamespacePermissionVerification? Child)>();
            foreach (var entry in page.Entries.Reverse())
            {
                var item = Item(entry.Original); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
                var projection = await RequireMemberProjectionAsync(entry.Original, token).ConfigureAwait(false);
                MirrorPulseNamespacePermissionVerification? child = null;
                if (snapshot.Exists) child = await _facts.InspectChildAsync(entry.Original, item, entry.NativeRemoteRevision, token).ConfigureAwait(false);
                else RequireActuallyMissing(item);
                observed.Add((entry, !snapshot.Exists, projection, child));
            }
            var after = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(remote.Parent, parent, guard, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, after);
            var parentProof = MirrorPulseRemoteRetirementBatchCoordinator.Verification(after); DateTimeOffset verified = DateTimeOffset.UtcNow;
            progress = await catalog.SettleNamespaceRemoteRetirementTreePageAsync(original.OperationId, observed.Select(member =>
                new MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement(1, original.OperationId, member.Original.Ordinal,
                    member.Original.Original.Context.Member.MemberId, member.Missing, member.Projection.IsTombstone,
                    member.Projection.RemoteRevision, verified, parentProof, member.Child)).ToArray(), token).ConfigureAwait(false);
            if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterMemberPage, token).ConfigureAwait(false);
            if (progress.NextOrdinal >= 0) throw new MirrorPulseRemoteTreeSettlementPendingException(original.OperationId, progress.NextOrdinal + 1);
        }
        var completedParent = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(remote.Parent, parent, guard, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, completedParent);
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(original.OperationId,
            MirrorPulseRemoteRetirementBatchCoordinator.Verification(completedParent), DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSettlement, token).ConfigureAwait(false);
    }

    private CloudItem Item(MirrorPulseNamespaceMemberProtectionSnapshot original) => original.Context.Member.IsDirectory
        ? fileSystem.GetDirectory(original.Context.Location.RelativePath) : fileSystem.GetFile(original.Context.Location.RelativePath);

    private async Task<CloudItemState> RequireMemberProjectionAsync(MirrorPulseNamespaceMemberProtectionSnapshot original, CancellationToken token)
    {
        var identity = original.Identity; var member = original.Context.Member;
        var item = await _facts.ReadItemAsync(identity.ItemId!.Value, token).ConfigureAwait(false);
        if (item is null || item.ItemId != identity.ItemId || item.RemoteId != identity.RemoteId ||
            (item.Kind == CloudItemKind.Directory) != member.IsDirectory || item.RelativePath.Replace('\\', '/') != original.Context.Location.RelativePath)
            throw new InvalidDataException("The original directory member lost its exact public SDK item projection.");
        return item;
    }

    private static void RequireActuallyMissing(CloudItem item)
    {
        try { _ = File.GetAttributes(item.FullPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new InvalidDataException("A native entry still occupies an original directory member's path.");
    }

    internal static CloudRemoteChange RequireChange(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        var change = MirrorPulseRemoteRetirementBatchCoordinator.RequireChange(instance, batch, ordinal, options);
        if (change.ItemKind != CloudItemKind.Directory) throw new NotSupportedException("An original SDK tree requires a directory Delete.");
        return change;
    }

    internal static void RequireOriginal(MirrorPulseNamespaceRetirementBatch original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        _ = RequireChange(instance, batch, ordinal, options);
        if (original.Version != 2 || original.RemoteEntry is not { } remote) throw new InvalidDataException("The directory has no original SDK tree owner.");
        MirrorPulseRemoteRetirementBatchCoordinator.RequireOriginal(new(2, original.OperationId, original.MemberId, original.RootId,
            remote.Child.Context.Member.LocalObject, remote.Child.Context.Location.RelativePath, true, remote.Child.Context.Member.OwnerSid,
            remote.Child.Context.Protection.RoleSid, original.PreparedAt)
        { RemoteEntry = remote }, instance, batch, ordinal, options);
    }
}
