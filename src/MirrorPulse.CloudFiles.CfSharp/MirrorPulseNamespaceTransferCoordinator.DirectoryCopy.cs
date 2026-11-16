using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<MirrorPulseDirectoryTransferCopy> CopyDirectoryAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token, Guid? targetRecovery = null) => actor.RunAsync(async stop =>
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        if (copy.Steps.All(step => step.AcceptedAt is not null)) return copy;
        var mutations = workers.Mutations ?? throw new NotSupportedException("Directory transfer requires the normal-user mutation transport.");
        if (MirrorPulseProductCatalog.CreateDirectoryTransferSourceAdmission(copy.Admission) is not null)
            _ = RequireDirectoryTransferDeletionTransport(copy.Admission, workers);
        return await WithDirectoryTransferGuardsAsync(copy.Admission, async () =>
        {
            await RequireOriginalDirectoryRemoteSourceAsync(copy.Admission, workers, stop).ConfigureAwait(false);
            var executor = new MirrorPulseDirectoryTransferCopyExecutor(catalog, inputs, workers.Uploads, mutations)
            { Checkpoint = workers.DirectoryCopyCheckpoint };
            return await executor.CopyAsync(operationId, stop).ConfigureAwait(false);
        }, stop, useCurrentProtection: targetRecovery is not null).ConfigureAwait(false);
    }, token);

    internal ValueTask<MirrorPulseDirectoryTransferTargetVerification> VerifyDirectoryTargetAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token, Guid? targetRecovery = null) => actor.RunAsync(async stop =>
    {
        var saved = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, stop).ConfigureAwait(false);
        if (saved is not null) return saved;
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        return await WithDirectoryTransferGuardsAsync(copy.Admission, async () =>
        {
            var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
            var verified = await verifier.ReadCurrentAsync(copy, null, stop).ConfigureAwait(false);
            return await catalog.RecordNamespaceDirectoryTransferTargetAsync(verified, stop, targetRecovery).ConfigureAwait(false);
        }, stop, useCurrentProtection: targetRecovery is not null).ConfigureAwait(false);
    }, token);

    internal ValueTask<bool> SettleObservedDirectoryTransferBirthAsync(Guid operationId, int ordinal, CancellationToken token) =>
        actor.RunAsync(async stop => await execution.RunNormalUserOperationAsync(async () =>
        {
            var admission = await catalog.ReadNamespaceDirectoryTransferAsync(operationId, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending target birth lost its original transfer.");
            var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
            RequireRetainedDirectoryTransferRoutes(admission, topology);
            var target = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending target birth lost its original reservation.");
            return await new MirrorPulseNamespaceTransferTargetPublisher(fileSystem, state, catalog, router, actor)
                .SettleObservedDirectoryTransferMemberAsync(target, stop).ConfigureAwait(false);
        }).ConfigureAwait(false), token);

    internal ValueTask<IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember>> PublishDirectoryTargetAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) =>
        actor.RunAsync(stop => PublishDirectoryTargetUnderActorAsync(operationId, workers, stop), token);

    private async ValueTask<IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember>> PublishDirectoryTargetUnderActorAsync(
        Guid operationId, MirrorPulseTransferWorkerServices workers, CancellationToken stop,
        Func<MirrorPulseDirectoryTransferAdmission, MirrorPulseDirectoryTransferTargetVerification, CancellationToken, Task>? afterPublication = null,
        Guid? targetRecovery = null, MirrorPulseDirectoryTransferTargetActivation? activation = null)
    {
        actor.AssertAccess();
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
            var publisher = new MirrorPulseNamespaceTransferTargetPublisher(fileSystem, state, catalog, router, actor,
                TargetPublicationCheckpoint, TargetCreationReceiptReady);
            var targetParent = targetRecovery is null ? admission.TargetParent :
                await ReadDirectoryTransferProtectionAsync(admission.TargetParent, stop, execution.RoleSid.Value).ConfigureAwait(false);
            var parents = new Dictionary<Guid, MirrorPulseNamespaceMemberProtectionSnapshot>
            {
                [admission.SourceParent.Context.Member.MemberId] = targetParent
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
                    bool registeredBeforeRecovery = targetRecovery is not null && target is not null &&
                        await catalog.ReadNamespaceMemberAsync(target.Birth.OperationId, stop).ConfigureAwait(false) is not null;
                    if (target is null)
                    {
                        var identity = await router.CreateCurrentFileIdentityAsync(root.InstanceId, root.UniquenessKey, remote.RemoteId, remote.Revision, stop)
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
                        target = await catalog.PrepareNamespaceDirectoryTransferBirthAsync(new(operationId, index, birth, plan)
                        { RecoveryInitializationId = targetRecovery }, stop)
                            .ConfigureAwait(false);
                    }
                    if (!registeredBeforeRecovery)
                        _ = await publisher.PublishMemberUnderActorAsync(target.Birth, target.Plan, parent, root,
                            remote.RemoteId, remote.Revision, stop,
                            directoryActivation: target.Birth.RoleSid == execution.RoleSid.Value ? null : activation).ConfigureAwait(false);
                    var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(target.Birth.OperationId, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The published directory transfer member lost its protection.");
                    CloudItem item = remote.IsDirectory ? fileSystem.GetDirectory(member.TargetRelativePath) : fileSystem.GetFile(member.TargetRelativePath);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    guards.Add(guard);
                    if (registeredBeforeRecovery)
                        await RequireDirectoryRetirementTargetAsync(item, target, snapshot, guard, stop).ConfigureAwait(false);
                    else
                        RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), snapshot);
                    published.Add(new(snapshot, member.Source.Depth));
                    if (remote.IsDirectory)
                        parents.Add(member.Source.Protection.Context.Member.MemberId, snapshot);
                }
                await RequireExactTransferDirectoryInventoryAsync(published, stop).ConfigureAwait(false);
                for (int index = 0; index < guards.Count; index++)
                    RequireNative(await guards[index].InspectAsync(stop).ConfigureAwait(false), published[index].Protection);
                if (afterPublication is not null)
                {
                    var freshTarget = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(copy, verified, stop)).ConfigureAwait(false);
                    await afterPublication(admission, freshTarget, stop).ConfigureAwait(false);
                }
                return (IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember>)published.AsReadOnly();
            }
            finally
            {
                for (int index = guards.Count - 1; index >= 0; index--) await guards[index].DisposeAsync().ConfigureAwait(false);
            }
        }, stop, publishingTarget: true, useCurrentProtection: targetRecovery is not null).ConfigureAwait(false);
    }

    private async Task<T> WithDirectoryTransferGuardsAsync<T>(MirrorPulseDirectoryTransferAdmission plan,
        Func<Task<T>> operation, CancellationToken stop, bool publishingTarget = false, bool useCurrentProtection = false)
    {
        actor.AssertAccess();
        var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
        var (sourceRoute, targetRoute) = RequireDirectoryTransferRoutes(topology, plan.Members[0].Source.Protection, plan.TargetParent);
        if (sourceRoute != plan.SourceRoute || targetRoute != plan.TargetRoute)
            throw new InvalidOperationException("The original directory transfer configuration changed.");

        T result = default!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            string role = execution.RoleSid.Value;
            var sourceProtection = useCurrentProtection
                ? await ReadDirectoryTransferProtectionAsync(plan.SourceParent, stop, role).ConfigureAwait(false) : plan.SourceParent;
            var targetProtection = useCurrentProtection
                ? await ReadDirectoryTransferProtectionAsync(plan.TargetParent, stop, role).ConfigureAwait(false) : plan.TargetParent;
            await using var sourceParent = await OpenTransferDirectoryGuardAsync(sourceProtection, stop).ConfigureAwait(false);
            await using var targetParent = await OpenTransferDirectoryGuardAsync(targetProtection, stop).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(stop).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(sourceBefore, sourceProtection);
            RequireNative(targetBefore, targetProtection);
            if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.SourceParent.Context.Member.MemberId, stop).ConfigureAwait(false) != sourceProtection ||
                await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.TargetParent.Context.Member.MemberId, stop).ConfigureAwait(false) != targetProtection)
                throw new InvalidOperationException("Directory transfer requires its current original parent protections.");
            var destination = fileSystem.GetDirectory(plan.Members[0].TargetRelativePath);
            if (!publishingTarget && (await destination.InspectAsync(stop).ConfigureAwait(false)).Exists)
                throw new IOException("An existing native object occupies the directory transfer target.");
            var guards = new List<MirrorPulseWindowsNamespacePermissionLease>(plan.Members.Count);
            var guardedTree = new List<MirrorPulseNamespaceProtectedTreeMember>(plan.Members.Count);
            var data = new List<(MirrorPulseTransferSourceDataLease Lease, MirrorPulseNamespaceImportContent Content)>();
            try
            {
                foreach (var member in plan.Members)
                {
                    var original = member.Source.Protection;
                    var current = useCurrentProtection
                        ? await ReadDirectoryTransferProtectionAsync(original, stop, role).ConfigureAwait(false) : original;
                    if (current.Context.Protection.RoleSid != role || current.Context.Member.OwnerSid != execution.OwnerSid.Value ||
                        await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Member.MemberId, stop).ConfigureAwait(false) != current)
                        throw new InvalidOperationException("A directory transfer source member changed its protection or location.");
                    string path = original.Context.Location.RelativePath;
                    CloudItem item = original.Context.Member.IsDirectory ? fileSystem.GetDirectory(path) : fileSystem.GetFile(path);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    guards.Add(guard);
                    RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), current);
                    guardedTree.Add(new(current, member.Source.Depth));
                    var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
                        original.Context.Member.MemberId, actor, stop).ConfigureAwait(false);
                    RequireSourceProjection(original, projection);
                    if (projection.RemoteRevision != member.SourceRevision)
                        throw new InvalidDataException("A directory transfer source changed its original official revision.");
                    var native = await item.InspectAsync(stop).ConfigureAwait(false);
                    if (!native.Exists || native.IsTombstone || native.LocalBinding is not { } binding ||
                        new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != original.Context.Member.LocalObject)
                        throw new InvalidDataException("A directory transfer source changed its native binding.");
                    if (native.IsPlaceholder && useCurrentProtection && original.Context.Protection.RoleSid != role)
                        _ = await CaptureDirectoryRetirementProjectionAsync(member, item, native, null, stop).ConfigureAwait(false);
                    else if (native.IsPlaceholder)
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
                await RequireExactTransferDirectoryInventoryAsync(guardedTree, stop).ConfigureAwait(false);
                result = publishingTarget
                    ? await operation().ConfigureAwait(false)
                    : await execution.RunNormalUserOperationAsync(operation).ConfigureAwait(false);
                foreach (var (lease, content) in data)
                    await lease.VerifyAsync(content.Length, content.Sha256, stop).ConfigureAwait(false);
                for (int index = 0; index < plan.Members.Count; index++)
                    RequireNative(await guards[index].InspectAsync(stop).ConfigureAwait(false), guardedTree[index].Protection);
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
