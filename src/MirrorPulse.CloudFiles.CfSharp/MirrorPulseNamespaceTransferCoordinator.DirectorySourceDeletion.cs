using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal ValueTask<MirrorPulseDirectoryTransferSourceDeletion> DeleteDirectorySourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token) => actor.RunAsync(async stop =>
    {
        var retained = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operationId, stop).ConfigureAwait(false);
        if (retained?.AcceptedAt is not null) return retained;
        _ = await PublishDirectoryTargetUnderActorAsync(operationId, workers, stop, async (admission, freshTarget, admitted) =>
        {
            retained = await execution.RunNormalUserOperationAsync(async () =>
            {
                var original = MirrorPulseProductCatalog.CreateDirectoryTransferSourceAdmission(admission);
                var transport = original is null ? null : RequireDirectoryTransferDeletionTransport(admission, workers);
                if (retained is null)
                {
                    await RequireOriginalDirectoryRemoteSourceAsync(admission, workers, admitted).ConfigureAwait(false);
                    AdapterAtomicDirectoryDeletionRequest? request = null;
                    if (original is not null)
                    {
                        var workerAdmission = await transport!.AdmitDirectoryTransferSourceAsync(original, admitted).ConfigureAwait(false);
                        request = MirrorPulseProductCatalog.CreateDirectoryTransferSourceDeleteRequest(original, workerAdmission);
                    }
                    retained = await catalog.StartNamespaceDirectoryTransferSourceDeletionAsync(operationId, freshTarget,
                        request, DateTimeOffset.UtcNow, admitted).ConfigureAwait(false);
                }
                if (retained.AcceptedAt is not null) return retained;
                var receipt = await transport!.DeleteDirectoryTransferSourceAsync(retained.Request!, admitted).ConfigureAwait(false);
                return await catalog.AcceptNamespaceDirectoryTransferSourceDeletionAsync(operationId, receipt,
                    DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return retained ?? throw new InvalidDataException("The original directory source deletion lost its retained outcome.");
    }, token);

    private static IMirrorPulseDirectoryTransferDeletionTransport RequireDirectoryTransferDeletionTransport(
        MirrorPulseDirectoryTransferAdmission admission, MirrorPulseTransferWorkerServices workers)
    {
        if (workers.Mutations is not IMirrorPulseDirectoryTransferDeletionTransport transport ||
            !transport.SupportsDirectoryTransferDeletion(admission.SourceRoute.InstanceId))
            throw new NotSupportedException("The original source adapter cannot safely retire a remote directory transfer.");
        return transport;
    }
}
