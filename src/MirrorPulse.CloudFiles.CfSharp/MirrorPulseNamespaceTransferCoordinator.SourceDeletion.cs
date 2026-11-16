using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    /// <summary>Deletes only the original accepted remote source while retaining its guarded local bytes and both fences.</summary>
    /// <remarks>This phase does not perform local retirement or acknowledge a journal. The original remote revision is required.</remarks>
    public ValueTask<MirrorPulseNamespaceTransferCopy> DeleteSourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) =>
        DeleteSourceAsync(operationId, workers, null, token);

    internal ValueTask<MirrorPulseNamespaceTransferCopy> DeleteSourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, MirrorPulseFileTransferRecovery? recovery, CancellationToken token) =>
        ExecuteSourceDeleteAsync(operationId, workers, retireLocal: false, recovery, token);

    private async ValueTask<MirrorPulseNamespaceTransferCopy> ExecuteSourceDeleteAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, bool retireLocal, MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (!retireLocal && copy.Plan.SourceRemoteRevision is null)
            throw new InvalidOperationException("Conditional source deletion requires the original remote revision.");
        if (copy.SourceDeletion?.AcceptedAt is not null && !retireLocal) return copy;
        if (copy.SourceDeletion is { AcceptedAt: null }) throw new MirrorPulseMutationAmbiguousException("The original source deletion has no owned acceptance receipt.");
        return await actor.RunAsync(async stop =>
        {
            var current = await ReadAsync(operationId, stop).ConfigureAwait(false);
            var retirement = retireLocal ? await catalog.ReadNamespaceRetirementAsync(current.Plan.DeleteOperationId, stop).ConfigureAwait(false) : null;
            if (retirement?.Phase == MirrorPulseNamespaceRetirementPhase.Completed) return current;
            current = await catalog.ValidateNamespaceTransferSourceDispositionAsync(operationId, recovery, allowLocalOnly: retireLocal, stop).ConfigureAwait(false);
            if (current.SourceDeletion?.AcceptedAt is not null && !retireLocal) return current;
            if (current.SourceDeletion is { AcceptedAt: null }) throw new MirrorPulseMutationAmbiguousException("The original source deletion cannot be dispatched again.");
            if (protocolVersion(current.Plan.SourceRoute.InstanceId) != 2 ||
                protocolVersion(current.Plan.TargetRoute.InstanceId) != current.Plan.TargetRoute.ProtocolVersion)
                throw new InvalidOperationException("The original negotiated transfer protocol changed before source deletion.");
            if (retirement?.StartedAt is not null && !(await fileSystem.GetFile(current.Plan.Source.Context.Location.RelativePath).InspectAsync(stop).ConfigureAwait(false)).Exists)
            {
                await RecoverRetiredSourceUnderActorAsync(current, retirement, workers, stop).ConfigureAwait(false);
                return current;
            }
            var members = recovery is not null
                ? await catalog.ReadPublishedFileTransferRecoveryMembersAsync(operationId, recovery, stop).ConfigureAwait(false)
                : current.TargetRecovery is { } birthRecovery
                    ? await catalog.ReadFileTransferRecoveryMembersAsync(operationId, birthRecovery, stop).ConfigureAwait(false) : null;
            var originalSource = members?.Source ?? current.Plan.Source;
            return await WithGuardsAsync(originalSource, members?.SourceParent ?? current.Plan.SourceParent, members?.TargetParent ?? current.Plan.TargetParent,
                current.Plan.TargetRelativePath, async (file, childMetadata) =>
                {
                    var source = await ReadSourceProjectionAsync(file, originalSource, stop).ConfigureAwait(false);
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
                    await using var originalInput = await execution.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(current.Plan, stop)).ConfigureAwait(false);
                    var verifier = new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories);
                    var freshTarget = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(current, stop)).ConfigureAwait(false);
                    if (current.Plan.SourceRemoteRevision is null)
                    {
                        await execution.RunNormalUserOperationAsync(() =>
                            RequireLocalOnlySourceAbsentAsync(current, verifier, workers.Stats, stop)).ConfigureAwait(false);
                        await RequireCurrentTargetAsync(current, target, targetFile, targetGuard, stop).ConfigureAwait(false);
                        _ = await ReadSourceProjectionAsync(file, originalSource, stop).ConfigureAwait(false);
                        await data.VerifyAsync(current.Plan.Length, current.Plan.Sha256, stop).ConfigureAwait(false);
                        if (current.SourceAbsence is null)
                            current = await catalog.RecordNamespaceTransferSourceAbsenceAsync(operationId, freshTarget,
                                target.Context.Protection.ProtectionId, DateTimeOffset.UtcNow, recovery, stop).ConfigureAwait(false);
                    }
                    else if (current.SourceDeletion?.AcceptedAt is null)
                    {
                        var mutations = workers.Mutations ?? throw new NotSupportedException("Source deletion requires the Host's normal-user mutation transport.");
                        string sourcePath = current.Plan.Source.Context.Location.RelativePath[(current.Plan.SourceRoute.DirectoryName.Length + 1)..];
                        await RequireRemoteSourceAsync(current, verifier, workers.Stats, sourcePath, stop).ConfigureAwait(false);
                        await RequireCurrentTargetAsync(current, target, targetFile, targetGuard, stop).ConfigureAwait(false);
                        _ = await ReadSourceProjectionAsync(file, originalSource, stop).ConfigureAwait(false);
                        await data.VerifyAsync(current.Plan.Length, current.Plan.Sha256, stop).ConfigureAwait(false);
                        current = await catalog.StartNamespaceTransferSourceDeleteAsync(operationId, freshTarget,
                            target.Context.Protection.ProtectionId, DateTimeOffset.UtcNow, recovery, stop).ConfigureAwait(false);
                        // Keep this same write-excluding object open through RPC and the durable correlated reply.
                        string? result = await execution.RunNormalUserOperationAsync(async () => await mutations.DeleteAsync(new(
                            current.Plan.SourceRoute.InstanceId, sourcePath, current.Plan.SourceRemoteRevision, false,
                            current.Plan.DeleteOperationId, current.Plan.SourceRoute.RootKey), stop).ConfigureAwait(false)).ConfigureAwait(false);
                        current = await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(operationId, result,
                            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    }
                    if (retireLocal) await RetireGuardedSourceAsync(current, retirement, originalSource,
                        file, childMetadata, target, targetFile, targetGuard, data, verifier, recovery, stop).ConfigureAwait(false);
                    return current;
                }, stop, allowOwnedTargetBirth: true, allowSourceRetirement: retireLocal).ConfigureAwait(false);
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
            var original = new CloudPlaceholderIdentity(copy.Plan.Source.Identity.ItemId!.Value, copy.Plan.Source.Identity.RemoteId!, remote.RemoteRevision);
            if (!MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(router.GetRegistration(route.InstanceId, route.RootKey), remote.RemoteId, original) ||
                await stats.StatAsync(new(route.InstanceId, path, route.RootKey), token).ConfigureAwait(false) != copy.Plan.SourceRemoteRevision)
                throw new IOException("Another remote object or revision occupies the original source path.");
        }).ConfigureAwait(false);
    }

    private static async Task RequireLocalOnlySourceAbsentAsync(MirrorPulseNamespaceTransferCopy copy, MirrorPulseTransferTargetVerifier verifier,
        IMirrorPulseWorkerStatTransport stats, CancellationToken token)
    {
        var route = copy.Plan.SourceRoute;
        string path = copy.Plan.Source.Context.Location.RelativePath[(route.DirectoryName.Length + 1)..];
        var request = new MirrorPulseWorkerStatRequest(route.InstanceId, path, route.RootKey);
        if (await stats.StatAsync(request, token).ConfigureAwait(false) is not null)
            throw new IOException("A remote object occupies the original local-only source name.");
        await verifier.RequireAbsentEntryAsync(route, path, token).ConfigureAwait(false);
        if (await stats.StatAsync(request, token).ConfigureAwait(false) is not null)
            throw new IOException("The original local-only source name changed during absence verification.");
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
        var plan = copy.TargetBirth!.Plan;
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        if (!MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(router.GetRegistration(copy.Plan.TargetRoute.InstanceId,
                copy.Plan.TargetRoute.RootKey), copy.TargetVerification!.RemoteId, identity) || identity.RemoteRevision != copy.AcceptedRevision)
            throw new InvalidDataException("The original target birth does not identify its accepted backend object.");
        if (!native.IsPlaceholder || native.SynchronizationState != CloudSynchronizationState.InSync || native.Length != copy.Plan.Length ||
            projection.ItemId != copy.TargetPublication!.ItemId || projection.RemoteRevision != copy.AcceptedRevision ||
            !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
            throw new InvalidDataException("Source deletion requires its unchanged original accepted native/public target.");
    }
}
