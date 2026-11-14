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
            await using var sourceParent = await OpenTransferDirectoryGuardAsync(plan.SourceParent, stop).ConfigureAwait(false);
            await using var targetParent = await OpenTransferDirectoryGuardAsync(plan.TargetParent, stop).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(stop).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(sourceBefore, plan.SourceParent);
            RequireNative(targetBefore, plan.TargetParent);

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

    private async Task<MirrorPulseDirectoryTransferRetirement> RetireDirectorySourceMemberAsync(
        MirrorPulseDirectoryTransferAdmission plan, int ordinal, MirrorPulseDirectoryTransferRetirement? retained,
        MirrorPulseDirectoryTransferTargetVerification fresh, CancellationToken token)
    {
        var member = plan.Members[ordinal];
        var original = member.Source.Protection;
        var parent = MirrorPulseProductCatalog.DirectoryTransferSourceParent(plan, ordinal);
        var item = DirectoryTransferItem(original.Context.Location.RelativePath, original.Context.Member.IsDirectory);
        await using var parentLease = await OpenTransferDirectoryGuardAsync(parent, token).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(token).ConfigureAwait(false);
        RequireNative(before, parent);
        if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Member.MemberId, token).ConfigureAwait(false) != original)
            throw new InvalidOperationException("Native retirement requires the current original local source.");

        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (native.Exists)
        {
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
            RequireNative(await guard.InspectAsync(token).ConfigureAwait(false), original);
            if (native.IsTombstone || native.Kind != item.Kind || native.LocalBinding is not { } binding ||
                original.Context.Member.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId))
                throw new InvalidDataException("The native retirement path belongs to another source object.");
            if (item is CloudDirectory directory)
                await foreach (var child in directory.EnumerateLocalChildrenAsync(cancellationToken: token).ConfigureAwait(false))
                    throw new IOException("Native directory transfer retirement requires an empty original directory.");
            await using var data = member.Content is null ? null : await execution.RunNormalUserOperationAsync(() =>
                Task.FromResult(MirrorPulseTransferSourceDataLease.Open(item.FullPath))).ConfigureAwait(false);
            if (data is not null) await data.VerifyAsync(member.Content!.Length, member.Content.Sha256, token).ConfigureAwait(false);

            var projection = await CaptureDirectoryRetirementProjectionAsync(member, item, native, token).ConfigureAwait(false);
            retained = await catalog.StartNamespaceDirectoryTransferRetirementAsync(plan.Command.CommandId, ordinal,
                projection, fresh, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
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

        var durable = CancellationToken.None;
        native = await item.InspectAsync(durable).ConfigureAwait(false);
        if (native.Exists || !native.IsTombstone || native.ItemId != retained.Projection.ItemId)
            throw new InvalidDataException("Native source retirement requires the retained item's actual CfSharp tombstone.");
        await using (var read = await state.OpenStore.BeginTransactionAsync(durable).ConfigureAwait(false))
        {
            var row = await read.Items.GetByItemIdAsync(retained.Projection.ItemId, durable).ConfigureAwait(false);
            RequireOriginalSourceProjection(retained.Projection, row, tombstone: true, item.Kind);
            await read.RollbackAsync(durable).ConfigureAwait(false);
        }
        var facts = await parentLease.InspectAsync(durable).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, facts);
        return await catalog.CompleteNamespaceDirectoryTransferRetirementAsync(plan.Command.CommandId, ordinal,
            new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt), DateTimeOffset.UtcNow, durable).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceTransferSourceProjection> CaptureDirectoryRetirementProjectionAsync(
        MirrorPulseDirectoryTransferMember member, CloudItem item, CloudItemSnapshot native, CancellationToken token)
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
        if (native.IsPlaceholder)
        {
            var identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
            if (identity.ItemId != row.ItemId || identity.RemoteId != row.RemoteId ||
                (identity.RemoteRevision ?? string.Empty) != (row.RemoteRevision ?? string.Empty))
                throw new InvalidDataException("The native source disagrees with the original SDK retirement projection.");
        }
        else if (item.Kind == CloudItemKind.Directory)
            throw new InvalidDataException("The original transfer directory is no longer a native placeholder.");
        await read.RollbackAsync(token).ConfigureAwait(false);
        return projection;
    }

    private async Task RequireDirectoryRetirementTargetAsync(CloudItem item, MirrorPulseDirectoryTransferTargetBirth birth,
        MirrorPulseNamespaceMemberProtectionSnapshot snapshot, MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
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
}
