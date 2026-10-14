using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Prepares a resident original and verified private input before atomic replacement admission.</summary>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceReplacementPreparationCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseLocalImportStagingStore inputs,
    Func<InstanceId, bool> mayDispatch, MirrorPulseNamespaceWorkAdmission? namespaceWork = null)
{
    public async ValueTask<MirrorPulseNamespaceReplacementExecution> PrepareAsync(Guid operationId, Guid memberId,
        string sourcePath, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || memberId == Guid.Empty) throw new ArgumentException("Operation and member IDs are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var historical = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (historical is not null)
        {
            if (historical.Intent.MemberId != memberId) throw new InvalidOperationException("The operation already belongs to another member.");
            if (historical.Action is null) throw new InvalidOperationException("The legacy partial admission requires recovery before preparation replay.");
            return historical;
        }
        var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The replacement member is not registered.");
        if (original.Context.Member.IsDirectory || original.Context.Member.RootId is not { } rootId)
            throw new InvalidOperationException("Replacement requires a registered file below a managed root.");
        var registration = router.Registrations.Single(root => root.RootId == rootId);
        using var admission = namespaceWork?.Enter([rootId]);
        var file = fileSystem.GetFile(original.Context.Location.RelativePath);
        await execution.RunNormalUserOperationAsync(async () =>
        {
            var before = await file.InspectAsync(cancellationToken).ConfigureAwait(false);
            RequireBinding(before, original);
            if (before.IsPlaceholder && before.ContentAvailability != CloudContentAvailability.FullyAvailable)
            {
                if (registration.State != RootRegistrationState.Active || !mayDispatch(registration.InstanceId))
                    throw new InvalidOperationException("An offline replacement target must already be resident.");
                await file.HydrateAsync(CloudFileRange.WholeFile, cancellationToken).ConfigureAwait(false);
                var warmed = await file.InspectAsync(cancellationToken).ConfigureAwait(false); RequireBinding(warmed, original);
                if (warmed.ContentAvailability != CloudContentAvailability.FullyAvailable)
                    throw new IOException("The original replacement target is not fully materialized.");
            }
        }).ConfigureAwait(false);
        var content = await execution.RunNormalUserOperationAsync(async () =>
        {
            string absoluteSource = Path.GetFullPath(sourcePath);
            string relativeSource = Path.GetRelativePath(fileSystem.GetDirectory(string.Empty).FullPath, absoluteSource).Replace('\\', '/');
            if (!Path.IsPathRooted(relativeSource) && relativeSource != ".." && !relativeSource.StartsWith("../", StringComparison.Ordinal))
            {
                var sourceRoute = router.ResolveCurrentPath(absoluteSource);
                var sourceRoot = router.GetRegistration(sourceRoute.InstanceId, sourceRoute.RootKey);
                if (sourceRoot.State != RootRegistrationState.Active || !mayDispatch(sourceRoot.InstanceId))
                {
                    var local = await fileSystem.GetFile(relativeSource).InspectAsync(cancellationToken).ConfigureAwait(false);
                    if (local.IsPlaceholder && local.ContentAvailability != CloudContentAvailability.FullyAvailable)
                        throw new InvalidOperationException("An offline replacement input must already be resident.");
                }
            }
            return await inputs.StageAsync(operationId, absoluteSource, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await using var input = await execution.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(operationId, content, cancellationToken)).ConfigureAwait(false);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            MirrorPulseNamespaceReplacementExecution result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var current = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, stop).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The original replacement member disappeared.");
                if (current != original || current.Context.Protection.RoleSid != execution.RoleSid.Value || current.Context.Member.OwnerSid != execution.OwnerSid.Value)
                    throw new InvalidOperationException("Replacement preparation requires unchanged current-role membership after materialization.");
                var parentProof = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(current.Context.Location.ParentMemberId
                    ?? throw new InvalidDataException("The admitted file has no current parent."), stop).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The current protected parent is missing.");
                await using var parent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                    fileSystem.GetDirectory(parentProof.Context.Location.RelativePath), router, stop).ConfigureAwait(false);
                await using var target = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, stop).ConfigureAwait(false);
                var child = await target.InspectAsync(stop).ConfigureAwait(false); var parentNow = await parent.InspectAsync(stop).ConfigureAwait(false);
                var snapshot = await file.InspectAsync(stop).ConfigureAwait(false); RequireBinding(snapshot, current);
                if (child.IsDirectory || child.LinkCount != 1 || child.LocalObject != current.Context.Member.LocalObject ||
                    child.OwnerSid != current.Context.Member.OwnerSid || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.ExpectedDacl, child.Dacl) ||
                    parentProof.Context.Protection.RoleSid != execution.RoleSid.Value || !parentNow.IsDirectory ||
                    parentNow.OwnerSid != parentProof.Context.Member.OwnerSid || parentNow.LocalObject != parentProof.Context.Member.LocalObject ||
                    !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentProof.ExpectedDacl, parentNow.Dacl) ||
                    snapshot.IsPlaceholder && snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable)
                    throw new InvalidDataException("Replacement preparation lost its resident original or protected parent.");
                CloudPlaceholderIdentity identity;
                if (snapshot.IsPlaceholder)
                    identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
                else
                {
                    if (current.Identity.ItemId is not { } itemId) throw new InvalidDataException("The original local identity is missing.");
                    var item = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore, memberId, actor, stop).ConfigureAwait(false);
                    if (item.State != MirrorPulseNamespaceMemberLocationState.Projected || item.RelativePath != current.Context.Location.RelativePath ||
                        item.RemoteId != current.Identity.RemoteId)
                        throw new InvalidDataException("An ordinary original must retain its existing official identity projection.");
                    identity = new(itemId, item.RemoteId!, item.RemoteRevision);
                }
                if (identity.ItemId != current.Identity.ItemId || identity.RemoteId != current.Identity.RemoteId)
                    throw new InvalidDataException("The original public identity disagrees with retained membership.");
                var now = DateTimeOffset.UtcNow;
                var intent = new MirrorPulseNamespaceReplacementIntent(1, operationId, current, Convert.ToBase64String(identity.Encode()), content, now);
                result = await catalog.PrepareNamespaceReplacementExecutionAsync(intent, new(1, operationId, now),
                    new(1, operationId, Guid.NewGuid(), child.LocalObject, child.OwnerSid, execution.RoleSid.Value,
                        MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution), current.ExpectedDacl, now), stop).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireBinding(CloudItemSnapshot snapshot, MirrorPulseNamespaceMemberProtectionSnapshot original)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.File || snapshot.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != original.Context.Member.LocalObject)
            throw new InvalidDataException("Materialization cannot substitute a different object for the registered original.");
    }
}
