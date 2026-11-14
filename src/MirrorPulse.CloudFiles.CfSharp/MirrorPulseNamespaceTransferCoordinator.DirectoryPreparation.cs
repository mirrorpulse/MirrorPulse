using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    /// <summary>Hydrates original files before the Host takes instance gates needed by the content provider.</summary>
    internal Task MaterializeDirectorySourceAsync(MirrorPulseNamespaceMemberProtectionSnapshot source,
        MirrorPulseNamespaceMemberProtectionSnapshot target, CancellationToken token) =>
        execution.RunNormalUserOperationAsync(async () =>
        {
            var topology = await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false);
            var (route, _) = RequireDirectoryTransferRoutes(topology, source, target);
            if (source.Context.Location.RelativePath == route.DirectoryName)
                throw new InvalidOperationException("A first-level managed root cannot be transferred.");
            var tree = await catalog.ReadNamespaceProtectedSubtreeAsync(source.Context.Member.MemberId, token).ConfigureAwait(false);
            if (tree[0].Protection != source) throw new InvalidOperationException("The directory source changed before materialization.");
            foreach (var member in tree)
                if (!member.Protection.Context.Member.IsDirectory)
                    await MaterializeFileAsync(member.Protection, token).ConfigureAwait(false);
        });

    internal ValueTask<MirrorPulseDirectoryTransferAdmission> PrepareDirectoryAsync(Guid operationId,
        MirrorPulseNamespaceMemberProtectionSnapshot source, MirrorPulseNamespaceMemberProtectionSnapshot targetParent,
        string targetName, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var historical = await catalog.ReadNamespaceDirectoryTransferAsync(operationId, stop).ConfigureAwait(false);
        if (historical is not null)
        {
            RequireDirectoryTransferCommand(historical, source.Context.Member.MemberId, targetParent.Context.Member.MemberId, targetName);
            return historical;
        }

        var parentId = source.Context.Location.ParentMemberId
            ?? throw new InvalidOperationException("The transfer source must have an original managed parent.");
        var sourceParent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parentId, stop).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory source parent is missing.");
        var topology = await catalog.ReadAdapterTopologyAsync(stop).ConfigureAwait(false);
        var (sourceRoute, targetRoute) = RequireDirectoryTransferRoutes(topology, source, targetParent);
        if (sourceParent.Context.Member.RootId != sourceRoute.RootId)
            throw new InvalidOperationException("A first-level managed root cannot be transferred.");
        var tree = await catalog.ReadNamespaceProtectedSubtreeAsync(source.Context.Member.MemberId, stop).ConfigureAwait(false);
        if (tree[0].Protection != source)
            throw new InvalidOperationException("The directory source changed before guarded admission.");
        var command = new MirrorPulseNamespaceTransferCommandOrigin(1, operationId, source.Context.Member.MemberId,
            targetParent.Context.Member.MemberId, targetName, DateTimeOffset.UtcNow);
        var targetDirectory = fileSystem.GetDirectory(targetParent.Context.Location.RelativePath);
        var destination = targetDirectory.GetDirectory(targetName);
        string destinationPath = destination.RelativePath.Replace('\\', '/');

        MirrorPulseDirectoryTransferAdmission prepared = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var sourceGuard = await OpenTransferDirectoryGuardAsync(sourceParent, stop).ConfigureAwait(false);
            await using var targetGuard = await OpenTransferDirectoryGuardAsync(targetParent, stop).ConfigureAwait(false);
            var sourceBefore = await sourceGuard.InspectAsync(stop).ConfigureAwait(false);
            var targetBefore = await targetGuard.InspectAsync(stop).ConfigureAwait(false);
            RequireNative(sourceBefore, sourceParent);
            RequireNative(targetBefore, targetParent);
            if ((await destination.InspectAsync(stop).ConfigureAwait(false)).Exists)
                throw new IOException("The native directory transfer destination is occupied.");

            var members = new List<MirrorPulseDirectoryTransferMember>(tree.Count);
            var guards = new List<MirrorPulseWindowsNamespacePermissionLease>(tree.Count);
            var data = new List<MirrorPulseTransferSourceDataLease>();
            try
            {
                string originalPath = source.Context.Location.RelativePath;
                foreach (var member in tree)
                {
                    var protection = member.Protection;
                    var context = protection.Context;
                    string sourcePath = context.Location.RelativePath;
                    if (context.Member.RootId != sourceRoute.RootId || context.Protection.RoleSid != execution.RoleSid.Value ||
                        context.Member.OwnerSid != execution.OwnerSid.Value)
                        throw new InvalidOperationException("Directory transfer requires every source member under the current protected role.");
                    CloudItem item = context.Member.IsDirectory ? fileSystem.GetDirectory(sourcePath) : fileSystem.GetFile(sourcePath);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    guards.Add(guard);
                    RequireNative(await guard.InspectAsync(stop).ConfigureAwait(false), protection);
                    var snapshot = await item.InspectAsync(stop).ConfigureAwait(false);
                    if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
                        new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != context.Member.LocalObject)
                        throw new InvalidDataException("The native source changed while its directory transfer membership was being proved.");
                    var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
                        context.Member.MemberId, actor, stop).ConfigureAwait(false);
                    RequireSourceProjection(protection, projection);
                    if (snapshot.IsPlaceholder)
                    {
                        var identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
                        if (identity.ItemId != projection.ItemId || identity.RemoteId != projection.RemoteId ||
                            (identity.RemoteRevision ?? string.Empty) != (projection.RemoteRevision ?? string.Empty))
                            throw new InvalidDataException("A directory transfer member changed its original native or official identity.");
                    }

                    Guid? contentId = null;
                    MirrorPulseNamespaceImportContent? content = null;
                    if (item is CloudFile file)
                    {
                        RequireBinding(snapshot, protection);
                        var input = await execution.RunNormalUserOperationAsync(() =>
                            Task.FromResult(MirrorPulseTransferSourceDataLease.Open(file.FullPath))).ConfigureAwait(false);
                        data.Add(input);
                        Guid id = Guid.NewGuid();
                        contentId = id;
                        content = await execution.RunNormalUserOperationAsync(() => input.StageAsync(inputs, id, stop)).ConfigureAwait(false);
                        await input.VerifyAsync(content.Length, content.Sha256, stop).ConfigureAwait(false);
                    }
                    else if (!snapshot.Exists || snapshot.IsTombstone || !snapshot.IsPlaceholder || snapshot.Kind != CloudItemKind.Directory)
                        throw new InvalidDataException("A transfer directory needs its original live public placeholder.");

                    if (sourcePath != originalPath && !sourcePath.StartsWith(originalPath + "/", StringComparison.Ordinal))
                        throw new InvalidDataException("A source member escaped the original transfer subtree.");
                    members.Add(new(member, destinationPath + sourcePath[originalPath.Length..],
                        projection.RemoteRevision ?? throw new InvalidOperationException("Directory source retirement requires each original backend revision."),
                        Guid.NewGuid(), Guid.NewGuid(), contentId, content));
                }
                await RequireExactTransferDirectoryInventoryAsync(tree, stop).ConfigureAwait(false);
                for (int index = 0; index < tree.Count; index++)
                    RequireNative(await guards[index].InspectAsync(stop).ConfigureAwait(false), tree[index].Protection);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceGuard.InspectAsync(stop).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetGuard.InspectAsync(stop).ConfigureAwait(false));
                if ((await destination.InspectAsync(stop).ConfigureAwait(false)).Exists)
                    throw new IOException("The native directory transfer destination changed during admission.");
                var now = DateTimeOffset.UtcNow;
                var feed = JournalFeed ?? throw new InvalidOperationException("The Host's running original journal is unavailable.");
                var originals = await new MirrorPulseTransferJournalCapture(feed).CaptureDirectoryAsync(operationId, members, stop).ConfigureAwait(false);
                prepared = await catalog.PrepareNamespaceDirectoryTransferAsync(new(1, command, sourceRoute, targetRoute,
                    sourceParent, targetParent, members, originals, now), stop).ConfigureAwait(false);
            }
            finally
            {
                for (int index = data.Count - 1; index >= 0; index--) await data[index].DisposeAsync().ConfigureAwait(false);
                for (int index = guards.Count - 1; index >= 0; index--) await guards[index].DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return prepared;
    }, token);

    private async Task<MirrorPulseWindowsNamespacePermissionLease> OpenTransferDirectoryGuardAsync(
        MirrorPulseNamespaceMemberProtectionSnapshot snapshot, CancellationToken token)
    {
        if (!snapshot.Context.Member.IsDirectory || snapshot.Context.Member.OwnerSid != execution.OwnerSid.Value ||
            snapshot.Context.Protection.RoleSid != execution.RoleSid.Value)
            throw new InvalidOperationException("The directory transfer parent is outside current Host protection.");
        return await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
            fileSystem.GetDirectory(snapshot.Context.Location.RelativePath), router, token).ConfigureAwait(false);
    }

    private (MirrorPulseTransferRoute Source, MirrorPulseTransferRoute Target) RequireDirectoryTransferRoutes(
        MirrorPulseAdapterTopology topology, MirrorPulseNamespaceMemberProtectionSnapshot source,
        MirrorPulseNamespaceMemberProtectionSnapshot target)
    {
        if (!source.Context.Member.IsDirectory || !target.Context.Member.IsDirectory)
            throw new ArgumentException("Directory transfer requires a directory source and target parent.");
        var original = CaptureRoute(topology, source);
        var destination = CaptureRoute(topology, target);
        if (original.InstanceId == destination.InstanceId || original.ProtocolVersion != 2 || destination.ProtocolVersion != 2 ||
            topology.Roots.Count(root => root.InstanceId == original.InstanceId) != 1 ||
            topology.Roots.Count(root => root.InstanceId == destination.InstanceId) != 1)
            throw new NotSupportedException("Directory transfer requires two original single-root instances with negotiated V2.");
        return (original, destination);
    }

    private static void RequireDirectoryTransferCommand(MirrorPulseDirectoryTransferAdmission admission,
        Guid sourceMember, Guid targetParent, string name)
    {
        if (admission.Command.SourceMemberId != sourceMember || admission.Command.TargetParentMemberId != targetParent ||
            admission.Command.TargetName != name)
            throw new InvalidOperationException("The original operation belongs to a different directory transfer request.");
    }

    private async Task RequireExactTransferDirectoryInventoryAsync(IReadOnlyList<MirrorPulseNamespaceProtectedTreeMember> tree,
        CancellationToken token)
    {
        var children = tree.ToLookup(item => item.Protection.Context.Location.ParentMemberId);
        foreach (var directory in tree.Where(item => item.Protection.Context.Member.IsDirectory))
        {
            var context = directory.Protection.Context;
            var expected = children[context.Member.MemberId].Select(child => child.Protection.Context.Location.RelativePath)
                .ToHashSet(StringComparer.Ordinal);
            await foreach (var item in fileSystem.GetDirectory(context.Location.RelativePath).EnumerateLocalChildrenAsync(cancellationToken: token)
                .ConfigureAwait(false))
                if (!expected.Remove(item.RelativePath.Replace('\\', '/')))
                    throw new InvalidDataException("Directory transfer cannot adopt an unregistered source descendant.");
            if (expected.Count != 0)
                throw new InvalidDataException("Directory transfer requires every registered source descendant.");
        }
    }
}
