using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Creates the original directory transfer target in parent-first order.</summary>
/// <remarks>The Host retains its actor, current native source guards, root leases and ordered instance
/// admissions. Each file uses the verified private handle through its first conditional upload.
/// This stage retains both root fences until verification, publication, retirement and journal settlement.</remarks>
internal sealed class MirrorPulseDirectoryTransferCopyExecutor(MirrorPulseProductCatalog catalog,
    MirrorPulseLocalTransferContentStore inputs, IMirrorPulseWorkerUploadTransport uploads,
    IMirrorPulseWorkerMutationTransport mutations, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal async Task<MirrorPulseDirectoryTransferCopy> CopyAsync(Guid transferId, CancellationToken token)
    {
        var copy = await catalog.ReadNamespaceDirectoryTransferCopyAsync(transferId, token).ConfigureAwait(false);
        var plan = copy.Admission;
        for (int ordinal = 0; ordinal < plan.Members.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var step = copy.Steps[ordinal];
            if (step.AcceptedAt is not null) continue;
            if (step.StartedAt is not null)
                throw new MirrorPulseMutationAmbiguousException("The original directory copy effect needs its correlated Worker receipt.");
            var member = plan.Members[ordinal];
            var route = plan.TargetRoute;
            string path = member.TargetRelativePath[(route.DirectoryName.Length + 1)..];
            string revision;
            if (member.Source.Protection.Context.Member.IsDirectory)
            {
                await catalog.StartNamespaceDirectoryTransferCopyAsync(plan, ordinal, _time.GetUtcNow(), token).ConfigureAwait(false);
                revision = await mutations.CreateDirectoryAsync(new(route.InstanceId, route.RootKey, path,
                    member.CopyOperationId, MustBeAbsent: true), token).ConfigureAwait(false);
            }
            else
            {
                await using var input = await inputs.OpenLeaseAsync(transferId, member, token).ConfigureAwait(false);
                await catalog.StartNamespaceDirectoryTransferCopyAsync(plan, ordinal, _time.GetUtcNow(), token).ConfigureAwait(false);
                revision = await uploads.UploadAsync(new(route.InstanceId, path, null, input.Data, input.Content.Length,
                    member.CopyOperationId, input.Content.Sha256, route.RootKey, DestinationMustBeAbsent: true)
                { RequireConditionalTargetSupport = true }, token).ConfigureAwait(false);
            }
            // Retain a successful correlated reply even when cancellation raced its return.
            await catalog.RecordNamespaceDirectoryTransferCopyAcceptedAsync(transferId, ordinal, member.CopyOperationId,
                revision, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        return await catalog.ReadNamespaceDirectoryTransferCopyAsync(transferId, token).ConfigureAwait(false);
    }
}
