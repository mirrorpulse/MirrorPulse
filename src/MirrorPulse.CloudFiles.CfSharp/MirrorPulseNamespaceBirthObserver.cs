using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains actual birth facts from public CfSharp inspection and an owned native lease.</summary>
/// <remarks>
/// This boundary neither creates objects nor repairs missing plans from current paths. The Host
/// owns admission, the namespace execution session and parent lifetime protection. A recorded
/// observation does not enable a root, verify its current ACL or acknowledge an upload.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespaceBirthObserver
{
    public static async Task<MirrorPulseNamespaceBirthObservation> RecordAsync(MirrorPulseProductCatalog catalog,
        Guid operationId, CloudItem item, MirrorPulseRootRouter router, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        var birth = await catalog.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        _ = await catalog.ReadNamespaceBirthStartAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth start is missing.");
        var expected = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        byte[] expectedIdentity = expected.Encode();
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(item, router, cancellationToken).ConfigureAwait(false);
        var facts = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.RootId != birth.RootId || facts.RelativePath != birth.RelativePath || facts.IsDirectory != birth.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != birth.IsDirectory || facts.LinkCount is not { } linkCount ||
            birth.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation && snapshot.SynchronizationState == CloudSynchronizationState.InSync ||
            !snapshot.PlaceholderIdentity.Span.SequenceEqual(expectedIdentity))
            throw new InvalidDataException("The retained native object does not match the original birth plan.");
        var observation = new MirrorPulseNamespaceBirthObservation(1, operationId, birth.RootId,
            facts.RelativePath, facts.IsDirectory, facts.LocalObject, plan.ItemId, plan.RemoteId, plan.RemoteRevision,
            true, snapshot.SynchronizationState == CloudSynchronizationState.InSync, linkCount,
            facts.OwnerSid, facts.Dacl, facts.ObservedAt);
        var retained = await catalog.ReadNamespaceBirthObservationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            // Preserve the first descriptor and time after later owned permission changes.
            // These historical facts are not a statement that the current ACL is protected.
            if (retained.LocalObject != observation.LocalObject || retained.OwnerSid != observation.OwnerSid ||
                retained.ItemId != observation.ItemId || retained.RemoteId != observation.RemoteId ||
                retained.RemoteRevision != observation.RemoteRevision || retained.IsDirectory != observation.IsDirectory ||
                retained.RootId != observation.RootId || retained.RelativePath != observation.RelativePath || linkCount != 1)
                throw new InvalidDataException("A later object cannot replace the first birth observation.");
            return retained;
        }
        return await catalog.RecordNamespaceBirthObservationAsync(observation, cancellationToken).ConfigureAwait(false);
    }
}
