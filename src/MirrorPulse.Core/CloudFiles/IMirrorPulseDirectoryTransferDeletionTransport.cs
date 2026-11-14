using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.CloudFiles;

internal interface IMirrorPulseDirectoryTransferDeletionTransport
{
    bool SupportsDirectoryTransferDeletion(InstanceId instanceId);
    ValueTask<AdapterDirectoryAdmission> AdmitDirectoryTransferSourceAsync(AdapterDirectoryAdmissionRequest original, CancellationToken token);
    ValueTask<AdapterAtomicDirectoryDeletionReceipt> DeleteDirectoryTransferSourceAsync(AdapterAtomicDirectoryDeletionRequest original, CancellationToken token);
}
