using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<MirrorPulseDirectoryTransferCopy> CopyDirectoryAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        if (copy.Steps.All(step => step.AcceptedAt is not null)) return copy;
        var mutations = workers.Mutations ?? throw new NotSupportedException("Directory transfer requires the normal-user mutation transport.");
        return await WithDirectoryTransferGuardsAsync(copy.Admission, async () =>
        {
            await RequireOriginalDirectoryRemoteSourceAsync(copy.Admission, workers, stop).ConfigureAwait(false);
            return await new MirrorPulseDirectoryTransferCopyExecutor(catalog, inputs, workers.Uploads, mutations)
                .CopyAsync(operationId, stop).ConfigureAwait(false);
        }, stop).ConfigureAwait(false);
    }, token);

    internal ValueTask<MirrorPulseDirectoryTransferTargetVerification> VerifyDirectoryTargetAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var saved = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, stop).ConfigureAwait(false);
        if (saved is not null) return saved;
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        return await WithDirectoryTransferGuardsAsync(copy.Admission, async () =>
        {
            var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
            var verified = await verifier.ReadCurrentAsync(copy, null, stop).ConfigureAwait(false);
            return await catalog.RecordNamespaceDirectoryTransferTargetAsync(verified, stop).ConfigureAwait(false);
        }, stop).ConfigureAwait(false);
    }, token);

    internal ValueTask<IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember>> PublishDirectoryTargetAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        var verified = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, stop).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Directory target publication requires the original whole target verification.");
        var admission = copy.Admission;
        return await WithDirectoryTransferGuardsAsync(admission, async () =>
        {
            var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
            _ = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(copy, verified, stop)).ConfigureAwait(false);
            var root = router.GetRegistration(admission.TargetRoute.InstanceId, admission.TargetRoute.RootKey);
            var publisher = new MirrorPulseNamespaceTransferTargetPublisher(fileSystem, state, catalog, router, actor);
            var parents = new Dictionary<Guid, MirrorPulseNamespaceMemberProtectionSnapshot>
            {
                [admission.SourceParent.Context.Member.MemberId] = admission.TargetParent
            };
            var guards = new List<MirrorPulseWindowsNamespacePermissionLease>(admission.Members.Count);
            var published = new List<MirrorPulseNamespaceProtectedTreeMember>(admission.Members.Count);
            try
            {
                for (int index = 0; index < admission.Members.Count; index++)
                {
                    var member = admission.Members[index];
                    var remote = verified.Members[index];
                    var parent = parents[member.Source.Protection.Context.Location.ParentMemberId!.Value];
                    var target = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, index, stop).ConfigureAwait(false);
                    if (target is null)
                    {
                        var identity = remote.IsDirectory
                            ? MirrorPulsePlaceholderIdentity.CreateForRoot(root, remote.RemoteId, remote.Revision).ToCfSharp()
                            : await router.CreateCurrentFileIdentityAsync(root.InstanceId, root.UniquenessKey, remote.RemoteId, remote.Revision, stop)
                                .ConfigureAwait(false);
                        _ = identity.Encode();
                        Guid birthId = Guid.NewGuid();
                        DateTimeOffset time = DateTimeOffset.UtcNow;
                        var birth = new MirrorPulseNamespaceBirthIntent(4, birthId, root.RootId,
                            parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty,
                            parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty, parent.Context.Member.LocalObject,
                            member.TargetRelativePath, remote.IsDirectory, MirrorPulseNamespaceBirthOrigin.TransferTarget,
                            parent.Context.Protection.RoleSid, time)
                        {
                            BornParent = parent.BornParent,
                            TransferEntry = MirrorPulseNamespaceTransferBirthReference.Create(verified, member.CopyOperationId)
                        };
                        var plan = new MirrorPulseNamespaceBirthPlan(remote.IsDirectory ? 1 : 2, birthId,
                            identity.ItemId, identity.RemoteId, identity.RemoteRevision, time)
                        { Content = remote.Content };
                        target = await catalog.PrepareNamespaceDirectoryTransferBirthAsync(new(operationId, index, birth, plan), stop)
                            .ConfigureAwait(false);
                    }
                    var (registered, _) = await publisher.PublishMemberUnderActorAsync(target.Birth, target.Plan, parent, root,
                        remote.RemoteId, remote.Revision, stop).ConfigureAwait(false);
                    var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(registered.MemberId, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The published directory transfer member lost its protection.");
                    CloudItem item = remote.IsDirectory ? fileSystem.GetDirectory(member.TargetRelativePath) : fileSystem.GetFile(member.TargetRelativePath);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    guards.Add(guard);
                    RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), snapshot);
                    published.Add(new(snapshot, member.Source.Depth));
                    if (remote.IsDirectory)
                        parents.Add(member.Source.Protection.Context.Member.MemberId, snapshot);
                }
                await RequireExactTransferDirectoryInventoryAsync(published, stop).ConfigureAwait(false);
                for (int index = 0; index < guards.Count; index++)
                    RequireNative(await guards[index].InspectAsync(stop).ConfigureAwait(false), published[index].Protection);
                return (IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember>)published.AsReadOnly();
            }
            finally
            {
                for (int index = guards.Count - 1; index >= 0; index--) await guards[index].DisposeAsync().ConfigureAwait(false);
            }
        }, stop, publishingTarget: true).ConfigureAwait(false);
    }, token);

    private async Task<T> WithDirectoryTransferGuardsAsync<T>(MirrorPulseDirectoryTransferAdmission plan,
        Func<Task<T>> operation, CancellationToken stop, bool publishingTarget = false)
    {
        actor.AssertAccess();
        var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
        var (sourceRoute, targetRoute) = RequireDirectoryTransferRoutes(topology, plan.Members[0].Source.Protection, plan.TargetParent);
        if (sourceRoute != plan.SourceRoute || targetRoute != plan.TargetRoute)
            throw new InvalidOperationException("The original directory transfer configuration changed.");

        T result = default!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var sourceParent = await OpenTransferDirectoryGuardAsync(plan.SourceParent, stop).ConfigureAwait(false);
            await using var targetParent = await OpenTransferDirectoryGuardAsync(plan.TargetParent, stop).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(stop).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(sourceBefore, plan.SourceParent);
            RequireNative(targetBefore, plan.TargetParent);
            if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.SourceParent.Context.Member.MemberId, stop).ConfigureAwait(false) != plan.SourceParent ||
                await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.TargetParent.Context.Member.MemberId, stop).ConfigureAwait(false) != plan.TargetParent)
                throw new InvalidOperationException("Directory transfer requires its current original parent protections.");
            var destination = fileSystem.GetDirectory(plan.Members[0].TargetRelativePath);
            if (!publishingTarget && (await destination.InspectAsync(stop).ConfigureAwait(false)).Exists)
                throw new IOException("An existing native object occupies the directory transfer target.");
            var guards = new List<MirrorPulseWindowsNamespacePermissionLease>(plan.Members.Count);
            var data = new List<(MirrorPulseTransferSourceDataLease Lease, MirrorPulseNamespaceImportContent Content)>();
            try
            {
                foreach (var member in plan.Members)
                {
                    var original = member.Source.Protection;
                    if (original.Context.Protection.RoleSid != execution.RoleSid.Value ||
                        original.Context.Member.OwnerSid != execution.OwnerSid.Value ||
                        await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Member.MemberId, stop).ConfigureAwait(false) != original)
                        throw new InvalidOperationException("A directory transfer source member changed its protection or location.");
                    string path = original.Context.Location.RelativePath;
                    CloudItem item = original.Context.Member.IsDirectory ? fileSystem.GetDirectory(path) : fileSystem.GetFile(path);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    guards.Add(guard);
                    RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), original);
                    var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
                        original.Context.Member.MemberId, actor, stop).ConfigureAwait(false);
                    RequireSourceProjection(original, projection);
                    if (projection.RemoteRevision != member.SourceRevision)
                        throw new InvalidDataException("A directory transfer source changed its original official revision.");
                    var native = await item.InspectAsync(stop).ConfigureAwait(false);
                    if (!native.Exists || native.IsTombstone || native.LocalBinding is not { } binding ||
                        new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != original.Context.Member.LocalObject)
                        throw new InvalidDataException("A directory transfer source changed its native binding.");
                    if (native.IsPlaceholder)
                    {
                        var identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
                        if (identity.ItemId != projection.ItemId || identity.RemoteId != projection.RemoteId ||
                            (identity.RemoteRevision ?? string.Empty) != (member.SourceRevision ?? string.Empty))
                            throw new InvalidDataException("A directory transfer source changed its public placeholder identity.");
                    }
                    if (!original.Context.Member.IsDirectory)
                    {
                        var lease = await execution.RunNormalUserOperationAsync(() =>
                            Task.FromResult(MirrorPulseTransferSourceDataLease.Open(item.FullPath))).ConfigureAwait(false);
                        data.Add((lease, member.Content!));
                        await lease.VerifyAsync(member.Content!.Length, member.Content.Sha256, stop).ConfigureAwait(false);
                    }
                    else if (!native.IsPlaceholder || native.Kind != CloudItemKind.Directory)
                        throw new InvalidDataException("A directory transfer requires its original directory placeholders.");
                }
                await RequireExactTransferDirectoryInventoryAsync(plan.Members.Select(member => member.Source).ToArray(), stop).ConfigureAwait(false);
                result = publishingTarget
                    ? await operation().ConfigureAwait(false)
                    : await execution.RunNormalUserOperationAsync(operation).ConfigureAwait(false);
                foreach (var (lease, content) in data)
                    await lease.VerifyAsync(content.Length, content.Sha256, stop).ConfigureAwait(false);
                for (int index = 0; index < plan.Members.Count; index++)
                    RequireNative(await guards[index].InspectAsync(stop).ConfigureAwait(false), plan.Members[index].Source.Protection);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceParent.InspectAsync(stop).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetParent.InspectAsync(stop).ConfigureAwait(false));
                if (!publishingTarget && (await destination.InspectAsync(stop).ConfigureAwait(false)).Exists)
                    throw new IOException("The native directory transfer target changed during remote copy.");
            }
            finally
            {
                for (int index = data.Count - 1; index >= 0; index--) await data[index].Lease.DisposeAsync().ConfigureAwait(false);
                for (int index = guards.Count - 1; index >= 0; index--) await guards[index].DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return result;
    }
}
