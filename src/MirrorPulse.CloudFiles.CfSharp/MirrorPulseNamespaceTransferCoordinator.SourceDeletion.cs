using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    /// <summary>Deletes only the original accepted remote source while retaining its guarded local bytes and both fences.</summary>
    /// <remarks>This phase does not perform local retirement or acknowledge a journal. A null original revision
    /// belongs to the separate local-only reconciliation path and cannot authorize an unconditional delete.</remarks>
    public async ValueTask<MirrorPulseNamespaceTransferCopy> DeleteSourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.SourceDeletion?.AcceptedAt is not null) return copy;
        if (copy.SourceDeletion is not null) throw new MirrorPulseMutationAmbiguousException("The original source deletion has no owned acceptance receipt.");
        var mutations = workers.Mutations ?? throw new NotSupportedException("Source deletion requires the Host's normal-user mutation transport.");
        return await actor.RunAsync(async stop =>
        {
            var current = await catalog.ValidateNamespaceTransferSourceDeleteAsync(operationId, stop).ConfigureAwait(false);
            if (current.SourceDeletion?.AcceptedAt is not null) return current;
            if (current.SourceDeletion is not null) throw new MirrorPulseMutationAmbiguousException("The original source deletion cannot be dispatched again.");
            if (protocolVersion(current.Plan.SourceRoute.InstanceId) != 2 ||
                protocolVersion(current.Plan.TargetRoute.InstanceId) != current.Plan.TargetRoute.ProtocolVersion)
                throw new InvalidOperationException("The original negotiated transfer protocol changed before source deletion.");
            return await WithGuardsAsync(current.Plan.Source, current.Plan.SourceParent, current.Plan.TargetParent,
                current.Plan.TargetRelativePath, async file =>
                {
                    var source = await ReadSourceProjectionAsync(file, current.Plan.Source, stop).ConfigureAwait(false);
                    if (source.RemoteRevision != current.Plan.SourceRemoteRevision)
                        throw new InvalidDataException("The original source projection changed before conditional source deletion.");
                    var target = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(current.TargetPublication!.MemberId, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The original published target member disappeared.");
                    var targetFile = fileSystem.GetFile(current.Plan.TargetRelativePath);
                    await using var targetGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetFile, router, stop).ConfigureAwait(false);
                    await RequireCurrentTargetAsync(current, target, targetFile, targetGuard, stop).ConfigureAwait(false);
                    await using var data = await execution.RunNormalUserOperationAsync(() =>
                        Task.FromResult(MirrorPulseTransferSourceDataLease.Open(file.FullPath))).ConfigureAwait(false);
                    await data.VerifyAsync(current.Plan.Length, current.Plan.Sha256, stop).ConfigureAwait(false);
                    var verifier = new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories);
                    var freshTarget = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(current, stop)).ConfigureAwait(false);
                    string sourcePath = current.Plan.Source.Context.Location.RelativePath[(current.Plan.SourceRoute.DirectoryName.Length + 1)..];
                    await RequireRemoteSourceAsync(current, verifier, workers.Stats, sourcePath, stop).ConfigureAwait(false);
                    await RequireCurrentTargetAsync(current, target, targetFile, targetGuard, stop).ConfigureAwait(false);
                    _ = await ReadSourceProjectionAsync(file, current.Plan.Source, stop).ConfigureAwait(false);
                    await data.VerifyAsync(current.Plan.Length, current.Plan.Sha256, stop).ConfigureAwait(false);
                    current = await catalog.StartNamespaceTransferSourceDeleteAsync(operationId, freshTarget,
                        target.Context.Protection.ProtectionId, DateTimeOffset.UtcNow, stop).ConfigureAwait(false);
                    // Keep this same write-excluding object open through RPC and the durable correlated reply.
                    string? result = await execution.RunNormalUserOperationAsync(async () => await mutations.DeleteAsync(new(
                        current.Plan.SourceRoute.InstanceId, sourcePath, current.Plan.SourceRemoteRevision, false,
                        current.Plan.DeleteOperationId, current.Plan.SourceRoute.RootKey), stop).ConfigureAwait(false)).ConfigureAwait(false);
                    return await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(operationId, result,
                        DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }, stop, allowOwnedTargetBirth: true).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private async Task RequireRemoteSourceAsync(MirrorPulseNamespaceTransferCopy copy, MirrorPulseTransferTargetVerifier verifier,
        IMirrorPulseWorkerStatTransport stats, string path, CancellationToken token)
    {
        await execution.RunNormalUserOperationAsync(async () =>
        {
            var route = copy.Plan.SourceRoute;
            if (await stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != copy.Plan.SourceRemoteRevision)
                throw new IOException("The original remote source revision changed before conditional deletion.");
            var remote = await verifier.ReadMetadataAsync(route, path, null, copy.Plan.SourceRemoteRevision!, token).ConfigureAwait(false);
            var scoped = router.CreateFileIdentity(route.InstanceId, route.RootKey, remote.RemoteId, remote.RemoteRevision);
            if (scoped.ItemId != copy.Plan.Source.Identity.ItemId || scoped.RemoteId != copy.Plan.Source.Identity.RemoteId ||
                await stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != copy.Plan.SourceRemoteRevision)
                throw new IOException("Another remote object or revision occupies the original source path.");
        }).ConfigureAwait(false);
    }

    private async Task RequireCurrentTargetAsync(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceMemberProtectionSnapshot target,
        CloudFile file, MirrorPulseWindowsNamespacePermissionLease guard, CancellationToken token)
    {
        if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(target.Context.Member.MemberId, token).ConfigureAwait(false) != target)
            throw new InvalidDataException("Target protection changed before conditional source deletion.");
        RequireNative(await guard.InspectAsync(token).ConfigureAwait(false), target);
        var native = await file.InspectAsync(token).ConfigureAwait(false); RequireBinding(native, target, requireResident: false);
        var projection = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore,
            target.Context.Member.MemberId, actor, token).ConfigureAwait(false);
        RequireSourceProjection(target, projection);
        var identity = router.CreateFileIdentity(copy.Plan.TargetRoute.InstanceId, copy.Plan.TargetRoute.RootKey,
            copy.TargetVerification!.RemoteId, copy.AcceptedRevision);
        if (!native.IsPlaceholder || native.SynchronizationState != CloudSynchronizationState.InSync || native.Length != copy.Plan.Length ||
            projection.ItemId != copy.TargetPublication!.ItemId || projection.RemoteRevision != copy.AcceptedRevision ||
            !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
            throw new InvalidDataException("Source deletion requires its unchanged original accepted native/public target.");
    }
}
