using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Executes an admitted member move and settles its product-owned name.</summary>
/// <remarks>
/// This is the first Host-facing composition boundary for controlled moves. MP uses CfSharp's
/// public move and state repositories; it does not reproduce CfSharp's private projection graph.
/// A native move can leave the durable product fence pending until both the native observation and
/// the official projection have been freshly verified. Directory completion remains a single-item
/// receipt until a future public subtree reconciliation receipt is available.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceMemberMoveCoordinator
{
    private readonly CloudFileSystem _fileSystem;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseRootRouter _router;
    private readonly MirrorPulseNamespaceExecutionSession _execution;
    private readonly MirrorPulseNamespaceActor _actor;

    public MirrorPulseNamespaceMemberMoveCoordinator(
        CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router,
        MirrorPulseNamespaceExecutionSession execution,
        MirrorPulseNamespaceActor actor)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
    }

    public async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        return await _actor.RunAsync(async token =>
        {
            _actor.AssertAccess();
            var intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
            var member = await _catalog.ReadNamespaceMemberAsync(intent.MemberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The admitted move member is missing.");
            await _catalog.RecordNamespaceMemberMoveStartAsync(
                new(1, operationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);

            CloudItem source = member.IsDirectory
                ? _fileSystem.GetDirectory(intent.Source.RelativePath)
                : _fileSystem.GetFile(intent.Source.RelativePath);
            string targetPath = intent.Target.RelativePath;
            int separator = targetPath.LastIndexOf('/');
            string targetParentPath = separator < 0 ? string.Empty : targetPath[..separator];
            string targetName = targetPath[(separator + 1)..];
            CloudDirectory target = targetParentPath.Length == 0
                ? _fileSystem.Root
                : _fileSystem.GetDirectory(targetParentPath);

            CloudItemMoveResult moved = null!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                moved = await source.MoveToAsync(target, targetName, cancellationToken: token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            MirrorPulseNamespaceMemberMoveObservation observation = await ReadNativeObservationAsync(
                moved.Item, target, intent, member, token).ConfigureAwait(false);
            await _catalog.RecordNamespaceMemberMoveObservationAsync(observation, token).ConfigureAwait(false);

            MirrorPulseNamespaceMemberLocationCandidate projection = await _execution.RunNormalUserOperationAsync(
                () => MirrorPulseNamespaceMemberLocator.ResolveAsync(_catalog, _state.OpenStore, member.MemberId, _actor, token)).ConfigureAwait(false);
            if (projection.State != MirrorPulseNamespaceMemberLocationState.Projected ||
                projection.ItemId != observation.ItemId || projection.RemoteId != observation.RemoteId ||
                !string.Equals(projection.RelativePath, observation.RelativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The official CfSharp projection did not settle at the admitted target.");

            MirrorPulseNamespacePermissionObject childFacts = default!;
            MirrorPulseNamespacePermissionObject parentFacts = default!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(moved.Item, _router, token).ConfigureAwait(false);
                await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(target, _router, token).ConfigureAwait(false);
                childFacts = await childLease.InspectAsync(token).ConfigureAwait(false);
                parentFacts = await parentLease.InspectAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var completion = new MirrorPulseNamespaceMemberMoveCompletion(
                1, operationId, observation.ItemId, observation.RemoteId, observation.RelativePath,
                observation.IsDirectory, DateTimeOffset.UtcNow,
                new(childFacts.LocalObject, childFacts.OwnerSid, childFacts.Dacl, childFacts.ObservedAt),
                new(parentFacts.LocalObject, parentFacts.OwnerSid, parentFacts.Dacl, parentFacts.ObservedAt),
                projection.RemoteRevision);
            return await _catalog.CompleteNamespaceMemberMoveAsync(completion, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceMemberMoveObservation> ReadNativeObservationAsync(
        CloudItem moved, CloudDirectory target, MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceMemberRegistration member, CancellationToken token)
    {
        MirrorPulseNamespacePermissionObject childFacts = default!;
        MirrorPulseNamespacePermissionObject parentFacts = default!;
        CloudItemSnapshot snapshot = null!;
        await _execution.RunNamespaceOperationAsync(async () =>
        {
            await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(moved, _router, token).ConfigureAwait(false);
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(target, _router, token).ConfigureAwait(false);
            childFacts = await childLease.InspectAsync(token).ConfigureAwait(false);
            parentFacts = await parentLease.InspectAsync(token).ConfigureAwait(false);
            snapshot = await moved.InspectAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            snapshot.ItemId is not { } itemId || snapshot.RemoteId is not { } remoteId ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != childFacts.LocalObject ||
            childFacts.LocalObject != intent.LocalObject || childFacts.RelativePath != intent.Target.RelativePath ||
            parentFacts.LocalObject != intent.Target.ParentLocalObject || childFacts.OwnerSid != intent.OwnerSid ||
            parentFacts.OwnerSid != intent.OwnerSid || (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory ||
            childFacts.LinkCount != 1)
            throw new InvalidDataException("The native move did not retain the admitted object, parent or protection.");
        return new(1, intent.OperationId, itemId, remoteId, snapshot.RemoteRevision, childFacts.RelativePath, member.IsDirectory, true,
            childFacts.LinkCount ?? 0,
            new(childFacts.LocalObject, childFacts.OwnerSid, childFacts.Dacl, childFacts.ObservedAt),
            new(parentFacts.LocalObject, parentFacts.OwnerSid, parentFacts.Dacl, parentFacts.ObservedAt));
    }
}
