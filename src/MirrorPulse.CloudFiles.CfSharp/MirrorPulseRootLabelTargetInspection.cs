using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains an original root Label target map only within the current namespace actor callback.</summary>
/// <remarks>
/// Metadata inspection neither publishes target routing nor authorizes a native move, source request,
/// content acceptance, ACL write or journal acknowledgement. Original public SDK reconciliation and
/// complete native/member checks remain the executor's responsibility.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRootLabelTargetInspection
{
    private readonly MirrorPulseNamespaceActor _actor;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseRootLabelTargetPaths _paths;
    private bool _active = true;

    private MirrorPulseRootLabelTargetInspection(MirrorPulseNamespaceActor actor, MirrorPulseRootRouter router,
        MirrorPulseRootLabelTargetPaths paths)
    { _actor = actor; _router = router; _paths = paths; }

    internal static async Task<T> RunAsync<T>(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
        MirrorPulseNamespaceActor actor, Guid operationId,
        Func<MirrorPulseRootLabelTargetInspection, CancellationToken, Task<T>> inspect, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(inspect);
        actor.AssertAccess();
        token.ThrowIfCancellationRequested();
        var intent = await catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
        if (intent.Version != 3)
            throw new InvalidOperationException("Target root Label inspection requires its original linked admission.");
        var history = await catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Target root Label inspection lost its original Label history.");
        var registration = (await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots
            .Single(root => root.RootId == intent.RootId);
        var proof = MirrorPulseManagedRootRenameCoordinator.Decode(history, registration);
        if (!history.Intent.IsPending || history.Proof!.DirectoryMoveEvidence != intent.DirectoryMoveEvidence ||
            proof.RootItemId != intent.ItemId || history.Proof.LocalObject != intent.LocalObject ||
            CloudPlaceholderIdentity.Decode(proof.ExpectedPlaceholderIdentity.Span).RemoteId != intent.RemoteId)
            throw new InvalidDataException("Target inspection cannot substitute the original Label proof or public identity.");
        var snapshot = await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Target inspection requires the original sealed subtree.");
        var paths = new MirrorPulseRootLabelTargetPaths(intent, snapshot, registration);
        paths.RequireCurrentRouter(router);
        actor.AssertAccess();
        var scope = new MirrorPulseRootLabelTargetInspection(actor, router, paths);
        try { return await inspect(scope, token).ConfigureAwait(false); }
        finally { scope._active = false; }
    }

    internal MirrorPulseNamespaceDirectoryMoveEntry RequireMember(CloudItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(!_active, this);
        _actor.AssertAccess();
        _paths.RequireCurrentRouter(_router);
        string path = item.RelativePath.Replace('\\', '/');
        if (_router.GetRelativePath(item.FullPath).Replace('\\', '/') != path)
            throw new InvalidDataException("Root Label target metadata must belong to the same original sync-root path.");
        return _paths.RequireMember(path, item.Kind == CloudItemKind.Directory);
    }

    internal MirrorPulse.Core.Contracts.RootId RootId => _paths.Registration.RootId;

    internal void RequireRouter(MirrorPulseRootRouter router)
    {
        if (!ReferenceEquals(_router, router))
            throw new InvalidOperationException("Root Label target inspection cannot be transferred to another router.");
    }

    internal void RequireNativeMember(CloudItem item, MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot)
    {
        var entry = RequireMember(item);
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != entry.LocalObject || facts.RootId != RootId || facts.OwnerSid != entry.OwnerSid ||
            facts.RelativePath != item.RelativePath.Replace('\\', '/') || facts.IsDirectory != entry.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("Root Label target inspection requires the exact original native member and owner.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, entry.Identity, requirePlaceholder: true);
    }
}
