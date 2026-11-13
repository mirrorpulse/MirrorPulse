using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.CloudFiles;

internal interface IMirrorPulseAtomicDirectoryDeletionTransport
{
    bool SupportsAtomicDirectoryDeletion(InstanceId instanceId);
    ValueTask<AdapterAtomicDirectoryDeletionReceipt> ExecuteAtomicDirectoryDeletionAsync(
        AdapterDirectoryAdmissionRequest original, CancellationToken token,
        IReadOnlyList<MirrorPulseWorkerChangeCommand>? originals = null);
}
