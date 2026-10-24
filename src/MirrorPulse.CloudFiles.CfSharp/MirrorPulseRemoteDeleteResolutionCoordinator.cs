using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Executes an admitted explicit SDK Delete and settles its separate protected product tree.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteDeleteResolutionCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteRetirementCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    private readonly MirrorPulseRemoteRetirementBatchCoordinator _facts = new(fileSystem, state, catalog, router, execution, actor);

    internal ValueTask<CloudRemoteApplyEntryStatus> ResolveAsync(MirrorPulseRemoteConflictActionIntent decision, CancellationToken token) =>
        actor.RunAsync(async admitted =>
        {
            CloudRemoteApplyEntryStatus result = default;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var retained = await catalog.ReadRemoteDeleteResolutionAsync(decision.CommandId, admitted).ConfigureAwait(false);
                var record = await ReadConflictAsync(decision.Conflict.ConflictId, admitted).ConfigureAwait(false);
                if (retained is null && record is null)
                    throw new KeyNotFoundException("The SDK conflict is missing without this command's owned delete admission.");
                if (record is not null) RequireOriginalConflict(decision, record.ConflictId, record.Conflict);
                if (decision.Action != MirrorPulseConflictAction.KeepRemote)
                    throw new NotSupportedException("Protected SDK deletion requires an explicit KeepRemote decision.");
                if (retained is not null && retained.Plan.Decision != decision)
                    throw new InvalidDataException("The explicit deletion changed its original product command.");
                var child = retained?.Plan.Entries[0].Original;
                if (child is null)
                {
                    CloudItem selected = record!.Conflict.Change.ItemKind == CloudItemKind.Directory
                        ? fileSystem.GetDirectory(decision.Conflict.RelativePath) : fileSystem.GetFile(decision.Conflict.RelativePath);
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(selected, router, admitted).ConfigureAwait(false);
                    var actual = await lease.InspectAsync(admitted).ConfigureAwait(false);
                    var member = await catalog.ReadNamespaceMemberByBindingAsync(actual.LocalObject, admitted).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The SDK Delete target has no current protected membership.");
                    child = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, admitted).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The SDK Delete target lost its current protection.");
                }
                var parent = retained?.Plan.Parent ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(
                    child.Context.Location.ParentMemberId ?? throw new InvalidDataException("An SDK conflict cannot delete the sync-root anchor."), admitted).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The SDK Delete target lost its protected direct parent.");
                bool reconcileCompletedEffects = record is null && retained?.StartedAt is not null;
                RequireCurrentRoute(decision, child, parent, reconcileCompletedEffects);
                if (record is not null && (record.Conflict.Change.ItemId != child.Identity.ItemId ||
                    record.Conflict.Change.RemoteId != child.Identity.RemoteId ||
                    (record.Conflict.Change.ItemKind == CloudItemKind.Directory) != child.Context.Member.IsDirectory))
                    throw new InvalidDataException("The explicit Delete changed its retained public target identity or kind.");
                if (retained?.Outcome is not null && record is not null)
                    throw new InvalidDataException("An observed terminal Delete cannot also own an unresolved SDK conflict.");
                var parentItem = fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parentItem, router, admitted).ConfigureAwait(false);
                var before = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(parent, parentItem, guard, admitted).ConfigureAwait(false);
                if (retained is null)
                {
                    var entries = await CaptureAsync(child.Context.Member.MemberId, admitted).ConfigureAwait(false);
                    retained = await catalog.PrepareRemoteDeleteResolutionAsync(new(1, decision, Fingerprint(record!), parent, entries,
                        DateTimeOffset.UtcNow), admitted).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
                }
                if (record is not null && Fingerprint(record) != retained.Plan.RemoteChangeFingerprint)
                    throw new InvalidDataException("The SDK conflict changed after the complete product tree was sealed.");
                await InspectInventoryAsync(retained, requireDeleted: retained.Outcome is not null || record is null, admitted).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(admitted).ConfigureAwait(false));
                if (retained.StartedAt is null)
                {
                    if (record is null) throw new InvalidDataException("Missing SDK conflict cannot acquire a retrospective start.");
                    retained = await catalog.StartRemoteDeleteResolutionAsync(decision.CommandId, DateTimeOffset.UtcNow, admitted).ConfigureAwait(false);
                }
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterStart, admitted).ConfigureAwait(false);
                if (retained.Outcome is null)
                {
                    MirrorPulseRemoteDeleteResolutionStatus status;
                    if (record is not null)
                    {
                        var observed = await fileSystem.ResolveRemoteConflictAsync(decision.Conflict.ConflictId,
                            new(CloudRemoteConflictDecision.KeepRemote), admitted).ConfigureAwait(false);
                        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply, CancellationToken.None).ConfigureAwait(false);
                        if (observed.Status is not (CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied))
                        { result = observed.Status; return; }
                        status = observed.Status == CloudRemoteApplyEntryStatus.Applied ? MirrorPulseRemoteDeleteResolutionStatus.Applied :
                            MirrorPulseRemoteDeleteResolutionStatus.AlreadyApplied;
                    }
                    else status = MirrorPulseRemoteDeleteResolutionStatus.Reconciled;
                    await InspectInventoryAsync(retained, requireDeleted: true, CancellationToken.None).ConfigureAwait(false);
                    if (await ReadConflictAsync(decision.Conflict.ConflictId, CancellationToken.None).ConfigureAwait(false) is not null)
                        throw new InvalidDataException("A completed explicit Delete still has its original unresolved SDK conflict.");
                    var after = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(parent, parentItem, guard, CancellationToken.None).ConfigureAwait(false);
                    MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, after);
                    retained = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(decision.CommandId,
                        new(status, DateTimeOffset.UtcNow, MirrorPulseRemoteRetirementBatchCoordinator.Verification(after)), CancellationToken.None).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterTreeOutcome, CancellationToken.None).ConfigureAwait(false);
                }
                await SettleAsync(retained, parentItem, guard, before, CancellationToken.None).ConfigureAwait(false);
                result = retained.Outcome!.Status == MirrorPulseRemoteDeleteResolutionStatus.Applied ? CloudRemoteApplyEntryStatus.Applied :
                    CloudRemoteApplyEntryStatus.AlreadyApplied;
            }).ConfigureAwait(false);
            return result;
        }, token);

    private Task<CloudRemoteConflictRecord?> ReadConflictAsync(Guid conflictId, CancellationToken token) =>
        execution.RunNormalUserOperationAsync(async () => await fileSystem.GetRemoteConflictAsync(conflictId, token).ConfigureAwait(false));

    internal static void RequireOriginalConflict(MirrorPulseRemoteConflictActionIntent decision, Guid conflictId, CloudRemoteConflict actual)
    {
        var expected = decision.Conflict;
        if (conflictId != expected.ConflictId || actual.Change.ChangeId != expected.ChangeId ||
            actual.Change.RelativePath.Replace('\\', '/') != expected.RelativePath.Replace('\\', '/') ||
            actual.Change.RemoteRevision != expected.RemoteRevision || actual.DetectedAt != expected.DetectedAt ||
            actual.Reason.ToString() != expected.Reason.ToString())
            throw new InvalidDataException("The public SDK conflict differs from the exact admitted product conflict epoch.");
        if (actual.Change.Kind != CloudRemoteChangeKind.Delete)
            throw new NotSupportedException("This protected conflict boundary requires the separate product lifecycle for non-Delete namespace changes.");
    }

    // This is an owned input digest only. It is never submitted to the SDK or written as SDK progress.
    private static string Fingerprint(CloudRemoteConflictRecord record) => Convert.ToHexString(new CloudRemoteChangeBatch(
        "mirrorpulse.conflict/" + record.ConflictId.ToString("N"), ReadOnlyMemory<byte>.Empty,
        [record.Conflict.Change], ReadOnlyMemory<byte>.Empty).Fingerprint.Span);

    private void RequireCurrentRoute(MirrorPulseRemoteConflictActionIntent decision, MirrorPulseNamespaceMemberProtectionSnapshot child,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, bool reconcileCompletedEffects)
    {
        var route = router.ResolveCurrentPath(decision.Conflict.RelativePath); var root = router.GetRegistration(route.InstanceId, route.RootKey);
        if (root.State != RootRegistrationState.Active || route.InstanceId != decision.Conflict.InstanceId ||
            child.Context.Member.RootId != root.RootId || parent.Context.Member.RootId != root.RootId ||
            child.Context.Location.ParentMemberId != parent.Context.Member.MemberId ||
            child.Context.Location.RelativePath != decision.Conflict.RelativePath.Replace('\\', '/') ||
            !reconcileCompletedEffects && (child.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Protection.RoleSid != execution.RoleSid.Value) ||
            child.Context.Member.OwnerSid != execution.OwnerSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidDataException("Explicit SDK deletion requires its active route, current role, owner and original direct parent.");
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeEntry>> CaptureAsync(Guid memberId, CancellationToken token)
    {
        var graph = await catalog.ReadNamespaceMemberTreeAsync(memberId, token).ConfigureAwait(false);
        if (graph.Count > 4096)
            throw new NotSupportedException("CfSharp preview.8 supports at most4096 members in one explicit Delete; no deletion owner was admitted.");
        var entries = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>(graph.Count);
        foreach (var node in graph)
        {
            var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(node.MemberId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The SDK conflict tree lost a protected original member.");
            var item = Item(original); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            _ = await _facts.InspectChildAsync(original, item, null, token, requireRevision: false).ConfigureAwait(false);
            var projected = await ProjectionAsync(original, token).ConfigureAwait(false);
            if (projected.IsTombstone) throw new InvalidDataException("A new explicit Delete cannot adopt a tombstoned member.");
            entries.Add(new(entries.Count, node.Depth, original, CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).RemoteRevision,
                projected.RemoteRevision));
        }
        return entries;
    }

    private async Task InspectInventoryAsync(MirrorPulseRemoteDeleteResolution retained, bool requireDeleted, CancellationToken token)
    {
        var plan = retained.Plan; var allowed = plan.Entries.ToDictionary(entry => entry.Original.Context.Location.RelativePath, StringComparer.OrdinalIgnoreCase);
        var root = Item(plan.Entries[0].Original); var rootSnapshot = await root.InspectAsync(token).ConfigureAwait(false);
        if (rootSnapshot.Exists)
        {
            var directories = new Queue<CloudDirectory>(); if (root is CloudDirectory directory) directories.Enqueue(directory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.RelativePath.Replace('\\', '/') }; int count = 1;
            while (directories.TryDequeue(out var current))
                await foreach (var item in current.EnumerateLocalChildrenAsync(cancellationToken: token).ConfigureAwait(false))
                {
                    string path = item.RelativePath.Replace('\\', '/');
                    if (++count > 65_536 || !seen.Add(path) || !allowed.TryGetValue(path, out var admitted))
                        throw new InvalidDataException("An unknown native child cannot join the sealed explicit Delete.");
                    _ = await _facts.InspectChildAsync(admitted.Original, item, admitted.NativeRemoteRevision, token).ConfigureAwait(false);
                    if (item is CloudDirectory nested) directories.Enqueue(nested);
                }
        }
        else RequireActuallyMissing(root);
        foreach (var entry in plan.Entries)
        {
            if (entry.Ordinal <= retained.NextOrdinal &&
                await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.Original.Context.Member.MemberId, token).ConfigureAwait(false) != entry.Original)
                throw new InvalidDataException("An unsettled explicit Delete member changed its name, identity or protection.");
            var item = Item(entry.Original); var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            var projection = await ProjectionAsync(entry.Original, token).ConfigureAwait(false);
            if (snapshot.Exists)
            {
                if (requireDeleted || projection.IsTombstone || projection.RemoteRevision != entry.OfficialRemoteRevision)
                    throw new InvalidDataException("A sealed explicit Delete member still exists or changed its official revision.");
                _ = await _facts.InspectChildAsync(entry.Original, item, entry.NativeRemoteRevision, token).ConfigureAwait(false);
            }
            else
            {
                RequireActuallyMissing(item);
                string? expected = entry.Ordinal == 0 ? plan.Decision.Conflict.RemoteRevision : entry.OfficialRemoteRevision;
                if (retained.StartedAt is null || (requireDeleted ? !projection.IsTombstone || projection.RemoteRevision != expected :
                    projection.RemoteRevision != (projection.IsTombstone ? expected : entry.OfficialRemoteRevision)))
                    throw new InvalidDataException("Missing members require the owned start and exact public SDK recovery projection.");
            }
        }
    }

    private async Task SettleAsync(MirrorPulseRemoteDeleteResolution retained, CloudDirectory parent,
        MirrorPulseWindowsNamespacePermissionLease guard, MirrorPulseNamespacePermissionObject before, CancellationToken token)
    {
        if (retained.CompletedAt is not null) return;
        var plan = retained.Plan;
        if (retained.NextOrdinal >= 0)
        {
            var observed = new List<(MirrorPulseNamespaceRemoteRetirementTreeEntry Entry, CloudItemState Projection)>();
            for (int ordinal = retained.NextOrdinal; ordinal >= Math.Max(0, retained.NextOrdinal - 63); ordinal--)
            {
                var entry = plan.Entries[ordinal]; var item = Item(entry.Original);
                if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new InvalidDataException("An explicit Delete member still exists before its settlement.");
                RequireActuallyMissing(item); observed.Add((entry, await ProjectionAsync(entry.Original, token).ConfigureAwait(false)));
            }
            var after = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(plan.Parent, parent, guard, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, after);
            var proof = MirrorPulseRemoteRetirementBatchCoordinator.Verification(after); var verified = DateTimeOffset.UtcNow;
            retained = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, observed.Select(value =>
                new MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement(1, plan.Decision.CommandId, value.Entry.Ordinal,
                    value.Entry.Original.Context.Member.MemberId, true, value.Projection.IsTombstone, value.Projection.RemoteRevision,
                    verified, proof, null)).ToArray(), token).ConfigureAwait(false);
            if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterMemberPage, token).ConfigureAwait(false);
            if (retained.NextOrdinal >= 0) throw new MirrorPulseRemoteTreeSettlementPendingException(plan.Decision.CommandId, retained.NextOrdinal + 1);
        }
        var final = await MirrorPulseRemoteRetirementBatchCoordinator.InspectParentAsync(plan.Parent, parent, guard, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, final);
        _ = await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, MirrorPulseRemoteRetirementBatchCoordinator.Verification(final),
            DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteRetirementCheckpoint.AfterSettlement, token).ConfigureAwait(false);
    }

    private CloudItem Item(MirrorPulseNamespaceMemberProtectionSnapshot original) => original.Context.Member.IsDirectory
        ? fileSystem.GetDirectory(original.Context.Location.RelativePath) : fileSystem.GetFile(original.Context.Location.RelativePath);

    private async Task<CloudItemState> ProjectionAsync(MirrorPulseNamespaceMemberProtectionSnapshot original, CancellationToken token)
    {
        var item = await _facts.ReadItemAsync(original.Identity.ItemId!.Value, token).ConfigureAwait(false);
        if (item is null || item.ItemId != original.Identity.ItemId || item.RemoteId != original.Identity.RemoteId ||
            (item.Kind == CloudItemKind.Directory) != original.Context.Member.IsDirectory ||
            item.RelativePath.Replace('\\', '/') != original.Context.Location.RelativePath)
            throw new InvalidDataException("The explicit Delete member lost its exact public SDK item projection.");
        return item;
    }

    private static void RequireActuallyMissing(CloudItem item)
    {
        try { _ = File.GetAttributes(item.FullPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new InvalidDataException("A native entry still occupies the explicit Delete member's path.");
    }
}
