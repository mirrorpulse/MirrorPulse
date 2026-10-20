using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains remote replay intent while its managed namespace is being recovered.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteNamespaceFence(MirrorPulseRootRouter router, MirrorPulseProductCatalog catalog,
    bool allowOriginalMoveRecovery = false)
{
    public async ValueTask<bool> CanPollAsync(RootRegistration root, CancellationToken cancellationToken)
    {
        RootRegistration current = router.GetRegistration(root.InstanceId, root.UniquenessKey);
        if (current.RootId != root.RootId || current.DirectoryName != root.DirectoryName ||
            current.State != RootRegistrationState.Active) return false;
        return !(await catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).Contains(root.RootId);
    }

    /// <summary>Allows only the immutable batch owning a pending move to reach outcome recovery.</summary>
    public async ValueTask<bool> CanReplayAsync(RootRegistration root, CloudRemoteChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        RootRegistration current = router.GetRegistration(root.InstanceId, root.UniquenessKey);
        if (current.RootId != root.RootId || current.DirectoryName != root.DirectoryName || current.State != RootRegistrationState.Active)
            return false;
        var updates = (await catalog.ReadPendingNamespaceRemoteUpdatesAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false))
            .Where(update => update.Plan.Child.Context.Member.RootId == root.RootId).ToArray();
        if (updates.Length != 0)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return false;
            if (updates.Length != 1) return false;
            var plan = updates[0].Plan;
            if (plan.Ordinal >= batch.Changes.Count || plan.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) ||
                plan.BatchEntryCount != batch.Changes.Count || MirrorPulseRemoteUpdateCoordinator.ChangeFingerprint(batch.Changes[plan.Ordinal]) != plan.ChangeFingerprint)
                return false;
            return await catalog.CanResumeNamespaceRemoteUpdateAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        var move = await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, cancellationToken).ConfigureAwait(false);
        if (move is null)
        {
            var births = (await catalog.ReadPendingNamespaceRemoteBirthsAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false))
                .Where(birth => birth.RootId == root.RootId).ToArray();
            if (births.Length == 0)
            {
                var retirements = (await catalog.ReadPendingNamespaceRemoteRetirementsAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false))
                    .Where(retirement => retirement.Intent.RootId == root.RootId).ToArray();
                if (retirements.Length == 0)
                {
                    var trees = (await catalog.ReadPendingNamespaceRemoteRetirementTreesAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false))
                        .Where(tree => tree.RootId == root.RootId).ToArray();
                    if (trees.Length == 0) return await CanPollAsync(root, cancellationToken).ConfigureAwait(false);
                    if (trees.Length != 1) return false;
                    var tree = trees[0]; var originalTree = tree.RemoteEntry!;
                    if (originalTree.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) ||
                        originalTree.BatchEntryCount != batch.Changes.Count || originalTree.Ordinal >= batch.Changes.Count) return false;
                    var treeChange = batch.Changes[originalTree.Ordinal];
                    if (treeChange.Kind != CloudRemoteChangeKind.Delete || treeChange.ItemKind != CloudItemKind.Directory ||
                        treeChange.ChangeId != originalTree.ChangeId || treeChange.ItemId != originalTree.ItemId || treeChange.RemoteId != originalTree.RemoteId ||
                        treeChange.PreviousRemoteRevision != originalTree.PreviousRemoteRevision || treeChange.RemoteRevision != originalTree.RemoteRevision ||
                        treeChange.RelativePath.Replace('\\', '/') != originalTree.Child.Context.Location.RelativePath) return false;
                    return await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(tree, cancellationToken).ConfigureAwait(false);
                }
                if (retirements.Length != 1) return false;
                var retirement = retirements[0]; var remoteDelete = retirement.Intent.RemoteEntry!;
                if (remoteDelete.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) ||
                    remoteDelete.BatchEntryCount != batch.Changes.Count || remoteDelete.Ordinal >= batch.Changes.Count) return false;
                var deletion = batch.Changes[remoteDelete.Ordinal];
                if (deletion.Kind != CloudRemoteChangeKind.Delete || deletion.ChangeId != remoteDelete.ChangeId ||
                    deletion.ItemId != remoteDelete.ItemId || deletion.RemoteId != remoteDelete.RemoteId ||
                    deletion.PreviousRemoteRevision != remoteDelete.PreviousRemoteRevision || deletion.RemoteRevision != remoteDelete.RemoteRevision ||
                    deletion.RelativePath.Replace('\\', '/') != retirement.Intent.RelativePath ||
                    (deletion.ItemKind == CloudItemKind.Directory) != retirement.Intent.IsDirectory) return false;
                return await catalog.CanResumeNamespaceRemoteRetirementAsync(retirement.Intent, cancellationToken).ConfigureAwait(false);
            }
            if (births.Length != 1) return false;
            var birth = births[0]; var original = birth.RemoteEntry!;
            if (original.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) ||
                original.BatchEntryCount != batch.Changes.Count || original.Ordinal >= batch.Changes.Count) return false;
            var entry = batch.Changes[original.Ordinal];
            if (entry.Kind is not (CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert) ||
                entry.ItemId != original.ItemId || entry.ChangeId != original.ChangeId || entry.RemoteId != original.RemoteId ||
                entry.RemoteRevision != original.RemoteRevision || entry.RelativePath.Replace('\\', '/') != birth.RelativePath ||
                (entry.ItemKind == CloudItemKind.Directory) != birth.IsDirectory) return false;
            return await catalog.CanResumeNamespaceRemoteBirthAsync(birth, cancellationToken).ConfigureAwait(false);
        }
        if (move.RemoteEntry is not { } remote || remote.InstanceId != root.InstanceId || remote.BatchId != batch.BatchId ||
            remote.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) || remote.BatchEntryCount != batch.Changes.Count ||
            remote.Ordinal >= batch.Changes.Count) return false;
        var change = batch.Changes[remote.Ordinal];
        if (change.Kind != CloudRemoteChangeKind.Move || change.ChangeId != remote.ChangeId || change.ItemId != move.ItemId ||
            change.RemoteId != move.RemoteId || change.PreviousRemoteRevision != remote.PreviousRemoteRevision ||
            change.RemoteRevision != remote.RemoteRevision || change.PreviousRelativePath?.Replace('\\', '/') != move.Source.RelativePath ||
            change.RelativePath.Replace('\\', '/') != move.Target.RelativePath ||
            (change.ItemKind == CloudItemKind.Directory) != (move.DirectoryMoveEvidence is not null)) return false;
        foreach (var entry in batch.Changes)
            foreach (string path in entry.PreviousRelativePath is { } previous ? new[] { entry.RelativePath, previous } : [entry.RelativePath])
            {
                var route = router.ResolveCurrentPath(path);
                if (route.InstanceId != root.InstanceId || router.GetRegistration(route.InstanceId, route.RootKey).RootId != root.RootId) return false;
            }
        return await catalog.CanResumeNamespaceRemoteMoveAsync(move, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnsureApplyAllowedAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        HashSet<RootId> pending = (await catalog.ReadNamespaceDispatchFencedRootsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
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
        foreach (RootId rootId in affected.Distinct().Where(pending.Contains))
        {
            var root = router.Registrations.Single(item => item.RootId == rootId);
            if (!allowOriginalMoveRecovery || !await CanReplayAsync(root, batch, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Remote changes must wait for managed root namespace recovery.");
        }
    }
}
