using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<IReadOnlyList<MirrorPulseDirectoryTransferRetirement>> RetireDirectorySourceAsync(
        Guid operationId, MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        var accepted = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, stop).ConfigureAwait(false);
        if (accepted?.AcceptedAt is null)
            throw new InvalidOperationException("Original directory source deletion must settle before native retirement.");
        var plan = copy.Admission;
        var rows = (await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, stop).ConfigureAwait(false))
            .ToDictionary(row => row.Ordinal);
        if (rows.Count == plan.Members.Count && rows[0].RetiredAt is not null)
            return (IReadOnlyList<MirrorPulseDirectoryTransferRetirement>)rows.Values.OrderByDescending(row => row.Ordinal).ToArray();

        var verified = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, stop).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original verified transfer target is missing.");
        var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
        var (sourceRoute, targetRoute) = RequireDirectoryTransferRoutes(topology, plan.Members[0].Source.Protection, plan.TargetParent);
        if (sourceRoute != plan.SourceRoute || targetRoute != plan.TargetRoute)
            throw new InvalidOperationException("The original directory transfer configuration changed.");
        await execution.RunNamespaceOperationAsync(async () =>
        {
            string role = execution.RoleSid.Value;
            var sourceProtection = await ReadDirectoryTransferProtectionAsync(plan.SourceParent, stop, role).ConfigureAwait(false);
            var targetProtection = await ReadDirectoryTransferProtectionAsync(plan.TargetParent, stop, role).ConfigureAwait(false);
            await using var sourceParent = await OpenTransferDirectoryGuardAsync(sourceProtection, stop).ConfigureAwait(false);
            await using var targetParent = await OpenTransferDirectoryGuardAsync(targetProtection, stop).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(stop).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(sourceBefore, sourceProtection);
            RequireNative(targetBefore, targetProtection);

            var targetGuards = new List<(MirrorPulseWindowsNamespacePermissionLease Lease, MirrorPulseNamespaceMemberProtectionSnapshot Snapshot)>();
            var targetTree = new List<MirrorPulseNamespaceProtectedTreeMember>(plan.Members.Count);
            var privateInputs = new List<IAsyncDisposable>();
            try
            {
                for (int ordinal = 0; ordinal < plan.Members.Count; ordinal++)
                {
                    var member = plan.Members[ordinal];
                    var target = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The directory transfer target lost its original birth.");
                    var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(target.Birth.OperationId, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The directory transfer target lost its original protection.");
                    var item = DirectoryTransferItem(target.Birth.RelativePath, target.Birth.IsDirectory);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    targetGuards.Add((guard, snapshot));
                    await RequireDirectoryRetirementTargetAsync(item, target, snapshot, guard, stop).ConfigureAwait(false);
                    targetTree.Add(new(snapshot, member.Source.Depth));
                    if (member.Content is not null)
                        privateInputs.Add(await execution.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(operationId, member, stop))
                            .ConfigureAwait(false));
                }
                await RequireExactTransferDirectoryInventoryAsync(targetTree, stop).ConfigureAwait(false);
                var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                    new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
                var fresh = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(copy, verified, stop)).ConfigureAwait(false);
                for (int ordinal = plan.Members.Count - 1; ordinal >= 0; ordinal--)
                {
                    if (rows.TryGetValue(ordinal, out var prior) && prior.RetiredAt is not null) continue;
                    rows[ordinal] = await RetireDirectorySourceMemberAsync(plan, ordinal, prior, fresh, stop).ConfigureAwait(false);
                }
                foreach (var (guard, snapshot) in targetGuards)
                    RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), snapshot);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceParent.InspectAsync(stop).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetParent.InspectAsync(stop).ConfigureAwait(false));
            }
            finally
            {
                for (int index = privateInputs.Count - 1; index >= 0; index--)
                    await privateInputs[index].DisposeAsync().ConfigureAwait(false);
                for (int index = targetGuards.Count - 1; index >= 0; index--)
                    await targetGuards[index].Lease.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return (IReadOnlyList<MirrorPulseDirectoryTransferRetirement>)rows.Values.OrderByDescending(row => row.Ordinal).ToArray();
    }, token);

    internal ValueTask<bool> SettleStartedDirectoryRetirementAsync(Guid operationId, CancellationToken token) =>
        actor.RunAsync(async stop => await execution.RunNormalUserOperationAsync(async () =>
        {
            var plan = await catalog.ReadNamespaceDirectoryTransferAsync(operationId, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The started directory retirement lost its original transfer.");
            var rows = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, stop).ConfigureAwait(false);
            if (rows.Count == 0 || rows[^1].RetiredAt is not null) return false;
            var started = rows[^1];

            var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
            RequireRetainedDirectoryTransferRoutes(plan, topology);
            var original = plan.Members[started.Ordinal].Source.Protection;
            var item = DirectoryTransferItem(original.Context.Location.RelativePath, original.Context.Member.IsDirectory);
            if ((await item.InspectAsync(stop).ConfigureAwait(false)).Exists) return false;

            // During startup the previous protection epoch is still present. Hold the
            // original parent while verifying the native effect and settling its name.
            var parent = await ReadDirectoryTransferProtectionAsync(MirrorPulseProductCatalog.DirectoryTransferSourceParent(plan,
                started.Ordinal), stop).ConfigureAwait(false);
            if (parent.Context.Member.OwnerSid != execution.OwnerSid.Value)
                throw new InvalidOperationException("The started directory retirement belongs to a different owner.");
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(parent.Context.Location.RelativePath), router, stop).ConfigureAwait(false);
            var before = await lease.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(before, parent);
            _ = await CompleteDirectorySourceRetirementAsync(item, started, lease, before, parent.Context.Protection.RoleSid,
                stop).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false), token);

    private async Task<MirrorPulseDirectoryTransferRetirement> RetireDirectorySourceMemberAsync(
        MirrorPulseDirectoryTransferAdmission plan, int ordinal, MirrorPulseDirectoryTransferRetirement? retained,
        MirrorPulseDirectoryTransferTargetVerification fresh, CancellationToken token)
    {
        var member = plan.Members[ordinal];
        var original = member.Source.Protection;
        string role = execution.RoleSid.Value;
        var current = await ReadDirectoryTransferProtectionAsync(original, token, role).ConfigureAwait(false);
        var parent = await ReadDirectoryTransferProtectionAsync(MirrorPulseProductCatalog.DirectoryTransferSourceParent(plan, ordinal),
            token, role).ConfigureAwait(false);
        var item = DirectoryTransferItem(original.Context.Location.RelativePath, original.Context.Member.IsDirectory);
        await using var parentLease = await OpenTransferDirectoryGuardAsync(parent, token).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(token).ConfigureAwait(false);
        RequireNative(before, parent);
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (native.Exists)
        {
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
            RequireNative(await guard.InspectAsync(token).ConfigureAwait(false), current);
            if (native.IsTombstone || native.Kind != item.Kind || native.LocalBinding is not { } binding ||
                original.Context.Member.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId))
                throw new InvalidDataException("The native retirement path belongs to another source object.");
            if (item is CloudDirectory directory)
                await foreach (var child in directory.EnumerateLocalChildrenAsync(cancellationToken: token).ConfigureAwait(false))
                    throw new IOException("Native directory transfer retirement requires an empty original directory.");
            await using var data = member.Content is null ? null : await execution.RunNormalUserOperationAsync(() =>
                Task.FromResult(MirrorPulseTransferSourceDataLease.Open(item.FullPath))).ConfigureAwait(false);
            if (data is not null) await data.VerifyAsync(member.Content!.Length, member.Content.Sha256, token).ConfigureAwait(false);

            var (projection, observedLocalFileId) = await CaptureDirectoryRetirementProjectionAsync(
                member, item, native, retained, token).ConfigureAwait(false);
            retained = await catalog.StartNamespaceDirectoryTransferRetirementAsync(plan.Command.CommandId, ordinal,
                projection, fresh, DateTimeOffset.UtcNow, token, role, observedLocalFileId).ConfigureAwait(false);
            if (SourceRetirementCheckpoint is not null)
                await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion, token).ConfigureAwait(false);
            await guard.DisposeAsync().ConfigureAwait(false);
            await item.DeleteAsync(token).ConfigureAwait(false);
            if (SourceRetirementCheckpoint is not null)
                await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion, CancellationToken.None).ConfigureAwait(false);
            if (data is not null)
                await data.VerifyAsync(member.Content!.Length, member.Content.Sha256, CancellationToken.None).ConfigureAwait(false);
        }
        else if (retained is null)
            throw new InvalidDataException("A missing directory transfer source has no original native retirement start.");

        return await CompleteDirectorySourceRetirementAsync(item, retained, parentLease, before, role, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<MirrorPulseDirectoryTransferRetirement> CompleteDirectorySourceRetirementAsync(CloudItem item,
        MirrorPulseDirectoryTransferRetirement retained, MirrorPulseWindowsNamespacePermissionLease parentLease,
        MirrorPulseNamespacePermissionObject before, string role, CancellationToken token)
    {
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (native.Exists || !native.IsTombstone || native.ItemId != retained.Projection.ItemId)
            throw new InvalidDataException("Native source retirement requires the retained item's actual CfSharp tombstone.");
        await using (var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var row = await read.Items.GetByItemIdAsync(retained.Projection.ItemId, token).ConfigureAwait(false);
            RequireOriginalSourceProjection(retained.CurrentProjection, row, tombstone: true, item.Kind);
            await read.RollbackAsync(token).ConfigureAwait(false);
        }
        var facts = await parentLease.InspectAsync(token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, facts);
        return await catalog.CompleteNamespaceDirectoryTransferRetirementAsync(retained.TransferId, retained.Ordinal,
            new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt), DateTimeOffset.UtcNow, token, role).ConfigureAwait(false);
    }

    private async Task<(MirrorPulseNamespaceTransferSourceProjection Projection, long? ObservedPlaceholderLocalFileId)> CaptureDirectoryRetirementProjectionAsync(
        MirrorPulseDirectoryTransferMember member, CloudItem item, CloudItemSnapshot native,
        MirrorPulseDirectoryTransferRetirement? retained, CancellationToken token)
    {
        var original = member.Source.Protection;
        var candidate = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
            original.Context.Member.MemberId, actor, token).ConfigureAwait(false);
        RequireSourceProjection(original, candidate);
        await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var row = await read.Items.GetByItemIdAsync(original.Identity.ItemId!.Value, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The retirement source lost its original official identity.");
        var projection = new MirrorPulseNamespaceTransferSourceProjection(1, member.DeleteOperationId,
            candidate.ItemId!.Value, candidate.RemoteId!, candidate.RelativePath!, member.SourceRevision, row.LocalFileId,
            original.Context.Member.LocalObject, DateTimeOffset.UtcNow);
        RequireOriginalSourceProjection(projection, row, tombstone: false, item.Kind);
        long? observedPlaceholderLocalFileId = null;
        if (native.IsPlaceholder)
        {
            var identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
            // Role recovery can convert the same ordinary file to a placeholder.
            // Its first SDK local ID can be recorded only when the live placeholder
            // and official row agree and the existing retirement owns this file.
            if (retained is { Projection.OfficialLocalFileId: null } && item.Kind == CloudItemKind.File &&
                native.SyncRootFileId is not null && native.LocalFileId is { } nativeLocalId && row.LocalFileId == nativeLocalId)
                observedPlaceholderLocalFileId = nativeLocalId;
            bool convertedWithoutRemoteRevision = observedPlaceholderLocalFileId is not null &&
                string.IsNullOrEmpty(identity.RemoteRevision);
            if (identity.ItemId != row.ItemId || identity.RemoteId != row.RemoteId ||
                (!convertedWithoutRemoteRevision && (identity.RemoteRevision ?? string.Empty) != (row.RemoteRevision ?? string.Empty)))
                throw new InvalidDataException("The native source disagrees with the original SDK retirement projection.");
        }
        else if (item.Kind == CloudItemKind.Directory)
            throw new InvalidDataException("The original transfer directory is no longer a native placeholder.");
        await read.RollbackAsync(token).ConfigureAwait(false);
        return (projection, observedPlaceholderLocalFileId);
    }

    private async Task RequireDirectoryRetirementTargetAsync(CloudItem item, MirrorPulseDirectoryTransferTargetBirth birth,
        MirrorPulseNamespaceMemberProtectionSnapshot snapshot, MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
        if (snapshot.Context.Protection.RoleSid != execution.RoleSid.Value)
            throw new InvalidOperationException("The original transfer target is outside current Host protection.");
        RequireNative(await guard.InspectAsync(token).ConfigureAwait(false), snapshot);
        var identity = new CloudPlaceholderIdentity(birth.Plan.ItemId, birth.Plan.RemoteId, birth.Plan.RemoteRevision);
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (!native.Exists || native.IsTombstone || !native.IsPlaceholder || native.Kind != item.Kind ||
            native.SynchronizationState != CloudSynchronizationState.InSync || !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()) ||
            (birth.Plan.Content is { } content && native.Length != content.Length))
            throw new InvalidDataException("Native source retirement requires every unchanged published target.");
        var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
            snapshot.Context.Member.MemberId, actor, token).ConfigureAwait(false);
        RequireSourceProjection(snapshot, projection);
        if (projection.ItemId != birth.Plan.ItemId || projection.RemoteId != birth.Plan.RemoteId ||
            projection.RemoteRevision != birth.Plan.RemoteRevision)
            throw new InvalidDataException("The target lost its original accepted official identity.");
    }

    private CloudItem DirectoryTransferItem(string path, bool directory) =>
        directory ? fileSystem.GetDirectory(path) : fileSystem.GetFile(path);

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadDirectoryTransferProtectionAsync(
        MirrorPulseNamespaceMemberProtectionSnapshot original, CancellationToken token, string? role = null)
    {
        var current = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Member.MemberId, token).ConfigureAwait(false);
        if (!MirrorPulseProductCatalog.MatchesDirectoryTransferProtection(original, current, role ?? current?.Context.Protection.RoleSid))
            throw new InvalidOperationException("The original directory transfer member lost its binding, position or protected role.");
        return current!;
    }
}
