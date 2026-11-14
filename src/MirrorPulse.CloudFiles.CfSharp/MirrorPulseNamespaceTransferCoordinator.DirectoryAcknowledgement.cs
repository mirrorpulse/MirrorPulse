using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<MirrorPulseDirectoryTransferAcknowledgement> AcknowledgeDirectoryAsync(
        Guid operationId, MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var retained = await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, stop).ConfigureAwait(false);
        if (retained?.AcknowledgedAt is not null) return retained;
        if (retained is not null) return await ReplayStartedDirectoryAcknowledgementUnderActorAsync(operationId, stop).ConfigureAwait(false);
        var journal = JournalFeed ?? throw new InvalidOperationException("Directory acknowledgement requires the Host's running journal.");
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, stop).ConfigureAwait(false);
        var plan = copy.Admission;
        var retired = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operationId, stop).ConfigureAwait(false);
        if (retired.Count != plan.Members.Count || retired.Any(row => row.RetiredAt is null))
            throw new InvalidOperationException("Directory acknowledgement requires every original local retirement.");
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
            await RequireDirectorySourceTombstonesAsync(plan, retired, stop).ConfigureAwait(false);

            var targetGuards = new List<MirrorPulseWindowsNamespacePermissionLease>(plan.Members.Count);
            try
            {
                var verified = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, stop).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Directory acknowledgement lost the verified target.");
                var nativeTree = new List<MirrorPulseNamespaceProtectedTreeMember>(plan.Members.Count);
                for (int ordinal = 0; ordinal < plan.Members.Count; ordinal++)
                {
                    var birth = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Directory acknowledgement lost an original target birth.");
                    var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(birth.Birth.OperationId, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Directory acknowledgement lost original target protection.");
                    var item = DirectoryTransferItem(birth.Birth.RelativePath, birth.Birth.IsDirectory);
                    var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                    targetGuards.Add(guard);
                    await RequireDirectoryRetirementTargetAsync(item, birth, snapshot, guard, stop).ConfigureAwait(false);
                    nativeTree.Add(new(snapshot, plan.Members[ordinal].Source.Depth));
                }
                await RequireExactTransferDirectoryInventoryAsync(nativeTree, stop).ConfigureAwait(false);
                var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                    new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
                var fresh = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(copy, verified, stop)).ConfigureAwait(false);
                retained = await catalog.StartNamespaceDirectoryTransferAcknowledgementAsync(operationId, fresh, DateTimeOffset.UtcNow, stop)
                    .ConfigureAwait(false);
                await InvokeOriginalDirectoryAcknowledgementAsync(journal, plan, stop).ConfigureAwait(false);
                var durable = CancellationToken.None;
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceParent.InspectAsync(durable).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetParent.InspectAsync(durable).ConfigureAwait(false));
                retained = await catalog.CompleteNamespaceDirectoryTransferAcknowledgementAsync(retained, DateTimeOffset.UtcNow, durable)
                    .ConfigureAwait(false);
            }
            finally
            {
                for (int index = targetGuards.Count - 1; index >= 0; index--)
                    await targetGuards[index].DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return retained ?? throw new InvalidDataException("Directory transfer lost its acknowledgement outcome.");
    }, token);

    internal ValueTask<MirrorPulseDirectoryTransferAcknowledgement> ReplayStartedDirectoryAcknowledgementAsync(
        Guid operationId, CancellationToken token) =>
        actor.RunAsync(stop => ReplayStartedDirectoryAcknowledgementUnderActorAsync(operationId, stop), token);

    private async ValueTask<MirrorPulseDirectoryTransferAcknowledgement> ReplayStartedDirectoryAcknowledgementUnderActorAsync(
        Guid operationId, CancellationToken token)
    {
        actor.AssertAccess();
        var original = await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original directory transfer acknowledgement disappeared.");
        if (original.AcknowledgedAt is not null) return original;
        var plan = await catalog.ReadNamespaceDirectoryTransferAsync(original.TransferId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The started directory transfer lost its original admission.");
        var topology = await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false);
        foreach (var route in new[] { plan.SourceRoute, plan.TargetRoute })
        {
            var root = router.GetRegistration(route.InstanceId, route.RootKey);
            if (root != topology.Roots.SingleOrDefault(candidate => candidate.RootId == route.RootId) ||
                MirrorPulseTransferRoute.Capture(topology.Instances.Single(instance => instance.InstanceId == route.InstanceId), root, route.ProtocolVersion) != route)
                throw new InvalidOperationException("The started directory transfer lost its original route.");
        }
        var journal = JournalFeed ?? throw new InvalidOperationException("Directory acknowledgement requires the Host's running journal.");
        var retired = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(original.TransferId, token).ConfigureAwait(false);
        MirrorPulseDirectoryTransferAcknowledgement completed = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            // The original start contains the protected target proof. Startup only
            // replays that exact SDK acknowledgement under the normal user, before a
            // new Host role changes the inherited native descriptors.
            await using var sourceParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(plan.SourceParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            await using var targetParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(plan.TargetParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(token).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(token).ConfigureAwait(false);
            RequireNative(sourceBefore, plan.SourceParent);
            RequireNative(targetBefore, plan.TargetParent);
            await RequireDirectorySourceTombstonesAsync(plan, retired, token).ConfigureAwait(false);
            await InvokeOriginalDirectoryAcknowledgementAsync(journal, plan, token).ConfigureAwait(false);
            var durable = CancellationToken.None;
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceParent.InspectAsync(durable).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetParent.InspectAsync(durable).ConfigureAwait(false));
            completed = await catalog.CompleteNamespaceDirectoryTransferAcknowledgementAsync(original, DateTimeOffset.UtcNow, durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return completed;
    }

    private async Task InvokeOriginalDirectoryAcknowledgementAsync(CloudLocalChangeFeed journal,
        MirrorPulseDirectoryTransferAdmission plan, CancellationToken token)
    {
        if (AcknowledgementCheckpoint is not null)
            await AcknowledgementCheckpoint(MirrorPulseTransferAcknowledgementCheckpoint.BeforeSdkAcknowledgement, token).ConfigureAwait(false);
        if (plan.Originals.Members.Count != 0)
            await execution.RunNormalUserOperationAsync(async () => await journal.AcknowledgeAsync(plan.Originals.Members
                .Select(member => new CloudLocalChangeAcknowledgement(member.OperationId, null)).ToArray(), token).ConfigureAwait(false)).ConfigureAwait(false);
        if (AcknowledgementCheckpoint is not null)
            await AcknowledgementCheckpoint(MirrorPulseTransferAcknowledgementCheckpoint.AfterSdkAcknowledgement, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RequireDirectorySourceTombstonesAsync(MirrorPulseDirectoryTransferAdmission plan,
        IReadOnlyList<MirrorPulseDirectoryTransferRetirement> retired, CancellationToken token)
    {
        var root = fileSystem.GetDirectory(plan.Members[0].Source.Protection.Context.Location.RelativePath);
        var native = await root.InspectAsync(token).ConfigureAwait(false);
        if (native.Exists || !native.IsTombstone || native.ItemId != plan.Members[0].Source.Protection.Identity.ItemId)
            throw new InvalidDataException("Directory acknowledgement requires the original source root tombstone.");
        await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (var member in retired)
        {
            var row = await read.Items.GetByItemIdAsync(member.Projection.ItemId, token).ConfigureAwait(false);
            var kind = plan.Members[member.Ordinal].Source.Protection.Context.Member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File;
            RequireOriginalSourceProjection(member.Projection, row, tombstone: true, kind);
        }
        await read.RollbackAsync(token).ConfigureAwait(false);
    }
}
