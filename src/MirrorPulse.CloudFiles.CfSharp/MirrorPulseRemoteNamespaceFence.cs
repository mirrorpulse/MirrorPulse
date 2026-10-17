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
        var move = await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, cancellationToken).ConfigureAwait(false);
        if (move is null)
        {
            var births = (await catalog.ReadPendingNamespaceRemoteBirthsAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false))
                .Where(birth => birth.RootId == root.RootId).ToArray();
            if (births.Length == 0) return await CanPollAsync(root, cancellationToken).ConfigureAwait(false);
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
