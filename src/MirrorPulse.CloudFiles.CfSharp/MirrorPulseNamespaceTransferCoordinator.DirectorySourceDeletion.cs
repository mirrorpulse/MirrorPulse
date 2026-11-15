using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<MirrorPulseDirectoryTransferSourceDeletion> DeleteDirectorySourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token, bool recoveredProtection = false) => actor.RunAsync(async stop =>
    {
        var retained = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, stop).ConfigureAwait(false);
        if (retained?.AcceptedAt is not null) return retained;
        if (recoveredProtection || retained is not null)
            return await WithPublishedDirectoryTransferTargetAsync(operationId, workers,
                (plan, fresh) => retained is null
                    ? StartDirectoryWorkerDeletionAsync(plan, fresh, workers, execution.RoleSid.Value, stop)
                    : CompleteDirectoryWorkerDeletionAsync(retained, RequireDirectoryTransferDeletionTransport(plan, workers), stop),
                stop).ConfigureAwait(false);
        _ = await PublishDirectoryTargetUnderActorAsync(operationId, workers, stop, async (admission, freshTarget, admitted) =>
        {
            retained = await execution.RunNormalUserOperationAsync(() =>
                StartDirectoryWorkerDeletionAsync(admission, freshTarget, workers, null, admitted)).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return retained ?? throw new InvalidDataException("The original directory source deletion lost its retained outcome.");
    }, token);

    private async Task<MirrorPulseDirectoryTransferSourceDeletion> StartDirectoryWorkerDeletionAsync(
        MirrorPulseDirectoryTransferAdmission admission, MirrorPulseDirectoryTransferTargetVerification freshTarget,
        MirrorPulseTransferWorkerServices workers, string? recoveredRole, CancellationToken token)
    {
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletionStart, token)
                .ConfigureAwait(false);
        var original = MirrorPulseProductCatalog.CreateDirectoryTransferSourceAdmission(admission);
        var transport = original is null ? null : RequireDirectoryTransferDeletionTransport(admission, workers);
        await RequireOriginalDirectoryRemoteSourceAsync(admission, workers, token).ConfigureAwait(false);
        AdapterAtomicDirectoryDeletionRequest? request = null;
        if (original is not null)
        {
            var workerAdmission = await transport!.AdmitDirectoryTransferSourceAsync(original, token).ConfigureAwait(false);
            request = MirrorPulseProductCatalog.CreateDirectoryTransferSourceDeleteRequest(original, workerAdmission);
        }
        var started = await catalog.StartNamespaceDirectoryTransferSourceDeletionAsync(admission.Command.CommandId, freshTarget,
            request, DateTimeOffset.UtcNow, token, recoveredRole).ConfigureAwait(false);
        return started.AcceptedAt is not null ? started :
            await CompleteDirectoryWorkerDeletionAsync(started, transport!, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseDirectoryTransferSourceDeletion> WithPublishedDirectoryTransferTargetAsync(
        Guid operationId, MirrorPulseTransferWorkerServices workers,
        Func<MirrorPulseDirectoryTransferAdmission, MirrorPulseDirectoryTransferTargetVerification, Task<MirrorPulseDirectoryTransferSourceDeletion>> delete,
        CancellationToken token)
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(operationId, token).ConfigureAwait(false);
        var verified = await catalog.ReadNamespaceDirectoryTransferTargetAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original source deletion lost its verified target.");
        return await WithDirectoryTransferGuardsAsync(copy.Admission, async () =>
        {
            var guards = new List<MirrorPulseWindowsNamespacePermissionLease>(copy.Admission.Members.Count);
            var targetTree = new List<MirrorPulseNamespaceProtectedTreeMember>(copy.Admission.Members.Count);
            try
            {
                for (int ordinal = 0; ordinal < copy.Admission.Members.Count; ordinal++)
                {
                    var target = await catalog.ReadNamespaceDirectoryTransferBirthAsync(operationId, ordinal, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The source deletion lost an original target birth.");
                    var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(target.Birth.OperationId, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The source deletion lost its published target.");
                    var item = DirectoryTransferItem(target.Birth.RelativePath, target.Birth.IsDirectory);
                    var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
                    guards.Add(lease);
                    await RequireDirectoryRetirementTargetAsync(item, target, snapshot, lease, token).ConfigureAwait(false);
                    targetTree.Add(new(snapshot, copy.Admission.Members[ordinal].Source.Depth));
                }
                await RequireExactTransferDirectoryInventoryAsync(targetTree, token).ConfigureAwait(false);
                var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                    new MirrorPulseTransferTargetVerifier(catalog, workers.Stats, workers.Ranges, workers.Directories), workers.Stats);
                var accepted = await execution.RunNormalUserOperationAsync(async () =>
                {
                    var fresh = await verifier.ReadCurrentAsync(copy, verified, token).ConfigureAwait(false);
                    return await delete(copy.Admission, fresh).ConfigureAwait(false);
                }).ConfigureAwait(false);
                for (int index = 0; index < guards.Count; index++)
                    RequireNative(await guards[index].InspectAsync(CancellationToken.None).ConfigureAwait(false), targetTree[index].Protection);
                return accepted;
            }
            finally
            {
                for (int index = guards.Count - 1; index >= 0; index--)
                    await guards[index].DisposeAsync().ConfigureAwait(false);
            }
        }, token, publishingTarget: true, replayingSourceDeletion: true).ConfigureAwait(false);
    }

    private async Task<MirrorPulseDirectoryTransferSourceDeletion> CompleteDirectoryWorkerDeletionAsync(
        MirrorPulseDirectoryTransferSourceDeletion retained, IMirrorPulseDirectoryTransferDeletionTransport transport, CancellationToken token)
    {
        var request = retained.Request ?? throw new InvalidDataException("The started source deletion lost its original Worker request.");
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.BeforeDirectoryWorkerDeletion, token)
                .ConfigureAwait(false);
        var receipt = await transport.DeleteDirectoryTransferSourceAsync(request, token).ConfigureAwait(false);
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.AfterDirectoryWorkerDeletionBeforeReceipt,
                CancellationToken.None).ConfigureAwait(false);
        return await catalog.AcceptNamespaceDirectoryTransferSourceDeletionAsync(retained.TransferId, receipt,
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private static IMirrorPulseDirectoryTransferDeletionTransport RequireDirectoryTransferDeletionTransport(
        MirrorPulseDirectoryTransferAdmission admission, MirrorPulseTransferWorkerServices workers)
    {
        if (workers.Mutations is not IMirrorPulseDirectoryTransferDeletionTransport transport ||
            !transport.SupportsDirectoryTransferDeletion(admission.SourceRoute.InstanceId))
            throw new NotSupportedException("The original source adapter cannot safely retire a remote directory transfer.");
        return transport;
    }
}
