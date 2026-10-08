using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains remote replay intent while its managed namespace is being recovered.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteNamespaceFence(MirrorPulseRootRouter router, MirrorPulseProductCatalog catalog)
{
    public async ValueTask<bool> CanPollAsync(RootRegistration root, CancellationToken cancellationToken)
    {
        RootRegistration current = router.GetRegistration(root.InstanceId, root.UniquenessKey);
        if (current.RootId != root.RootId || current.DirectoryName != root.DirectoryName ||
            current.State != RootRegistrationState.Active) return false;
        return !(await catalog.ReadManagedRootRenamesAsync(cancellationToken).ConfigureAwait(false))
            .Any(intent => intent.RootId == root.RootId && intent.IsPending);
    }

    public async ValueTask EnsureApplyAllowedAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        HashSet<RootId> pending = (await catalog.ReadManagedRootRenamesAsync(cancellationToken).ConfigureAwait(false))
            .Where(intent => intent.IsPending).Select(intent => intent.RootId).ToHashSet();
        if (pending.Count == 0) return;
        IEnumerable<RootId> affected = batch.Changes.Count == 0
            ? router.Registrations.Where(root => root.InstanceId == instance).Select(root => root.RootId)
            : batch.Changes.SelectMany(change => change.PreviousRelativePath is { } previous
                ? new[] { change.RelativePath, previous } : [change.RelativePath]).Select(path =>
                {
                    MirrorPulseRoutedItem routed = router.ResolveCurrentPath(path);
                    if (routed.InstanceId != instance)
                        throw new InvalidDataException("A remote batch belongs to another Adapter instance.");
                    return router.GetRegistration(routed.InstanceId, routed.RootKey).RootId;
                });
        if (affected.Any(pending.Contains))
            throw new InvalidOperationException("Remote changes must wait for managed root namespace recovery.");
    }
}
