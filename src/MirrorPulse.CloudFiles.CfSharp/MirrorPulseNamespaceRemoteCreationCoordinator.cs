using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Creates a source placeholder through public CfSharp APIs and registers its actual protected birth.</summary>
/// <remarks>Source metadata must already be fetched outside the actor. An admitted start is never
/// repeated when its native outcome is unknown. This does not acknowledge a local journal or grant Host readiness.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceRemoteCreationCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    public ValueTask<MirrorPulseNamespaceMemberRegistration> CreateAsync(Guid operationId, Guid parentMemberId,
        CloudPlaceholderSpec specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (operationId == Guid.Empty || parentMemberId == Guid.Empty) throw new ArgumentException("Birth and parent member IDs are required.");
        return actor.RunAsync(token => CreateUnderActorAsync(operationId, parentMemberId, specification, token), cancellationToken);
    }

    internal async ValueTask<MirrorPulseNamespaceMemberRegistration> CreateUnderActorAsync(Guid operationId, Guid parentMemberId,
        CloudPlaceholderSpec specification, CancellationToken token)
    {
        actor.AssertAccess();
        bool directory = specification.Kind == CloudItemKind.Directory;
        if (specification.Kind is not (CloudItemKind.Directory or CloudItemKind.File))
            throw new ArgumentException("Remote creation requires a file or directory specification.", nameof(specification));
        var parentMember = await catalog.ReadNamespaceMemberAsync(parentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The source population parent is not registered.");
        if (!parentMember.IsDirectory || parentMember.RootId is null && parentMember.ParentMemberId is not null)
            throw new InvalidOperationException("Source population requires a registered managed directory or the sync-root anchor.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(parentMemberId, token).ConfigureAwait(false);
        var parent = fileSystem.GetDirectory(location.RelativePath);
        CloudItem item = directory ? parent.GetDirectory(specification.Name) : parent.GetFile(specification.Name);
        string path = item.RelativePath.Replace('\\', '/');
        var route = router.Resolve(path, specification.Identity.Encode());
        var rootId = router.GetRegistration(route.InstanceId, route.RootKey).RootId;
        if (parentMember.RootId is { } parentRoot ? rootId != parentRoot : !directory || route.RelativePath.Length != 0)
            throw new InvalidDataException("The source identity belongs to another managed root.");
        var retained = await catalog.ReadNamespaceBirthAsync(operationId, token).ConfigureAwait(false);
        var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, token).ConfigureAwait(false);
        if (retained is not null && (retained.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation || retained.RootId != rootId ||
            retained.RelativePath != path || retained.IsDirectory != directory || retained.ParentLocalObject != parentMember.LocalObject) ||
            plan is not null && (plan.ItemId != specification.Identity.ItemId || plan.RemoteId != specification.Identity.RemoteId ||
                plan.RemoteRevision != specification.Identity.RemoteRevision || plan.Content is not null))
            throw new InvalidOperationException("The birth ID already identifies another source publication.");
        if (await catalog.ReadNamespaceMemberAsync(operationId, token).ConfigureAwait(false) is { } historical)
        {
            if (retained is null || plan is null || historical.ParentMemberId != parentMemberId || historical.OriginId != operationId ||
                historical.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
                throw new InvalidDataException("Source birth replay lost its original parent or provenance.");
            return historical;
        }
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        if (proof.RoleSid != execution.RoleSid.Value || proof.OwnerSid != execution.OwnerSid.Value || proof.RelativePath != location.RelativePath)
            throw new InvalidOperationException("Source publication requires current-role parent protection at its committed position.");
        if ((await catalog.ReadNamespaceRecoveryRootsAsync(token).ConfigureAwait(false)).Contains(rootId))
            throw new InvalidOperationException("The source publication root has unresolved namespace recovery.");
        MirrorPulseNamespaceMemberRegistration result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            if (retained is null && parentMember.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth &&
                location.CurrentPositionOperationId is not null)
            {
                var epoch = await catalog.ReadLatestNamespaceBirthProtectionAsync(parentMember.OriginId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The placed source parent lost its protection epoch.");
                if (epoch.Placement?.PositionOperationId != location.CurrentPositionOperationId)
                {
                    _ = await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, Guid.NewGuid(),
                        parentMemberId, parent, parent.Parent ?? throw new InvalidDataException("A placed parent requires its ancestor."),
                        router, actor, token).ConfigureAwait(false);
                    proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
                }
            }
            var birth = retained ?? new MirrorPulseNamespaceBirthIntent(proof.BornParent is null ? 1 : 2, operationId,
                rootId, proof.BornParent is null ? parentMember.OriginId : Guid.Empty, proof.PermissionOperationId,
                parentMember.LocalObject, path, directory, MirrorPulseNamespaceBirthOrigin.RemotePopulation, execution.RoleSid.Value, DateTimeOffset.UtcNow)
            { BornParent = proof.BornParent };
            var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent, proof, guard, token).ConfigureAwait(false);
            if (retained is null)
            {
                if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists) throw new IOException("The source destination is occupied.");
                birth = await catalog.PrepareNamespaceBirthAsync(birth, token).ConfigureAwait(false);
            }
            plan ??= await catalog.PrepareNamespaceBirthPlanAsync(new(1, operationId, specification.Identity.ItemId,
                specification.Identity.RemoteId, specification.Identity.RemoteRevision, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            if (await catalog.ReadNamespaceBirthStartAsync(operationId, token).ConfigureAwait(false) is null)
            {
                if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists) throw new IOException("The source destination is occupied.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(token).ConfigureAwait(false));
                _ = await catalog.RecordNamespaceBirthStartAsync(new(1, operationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
                // The mature API owns native creation and its official SQLite projection.
                // If it fails after creation, keep the original intent and never recreate by path.
                var created = await parent.CreatePlaceholdersAsync([specification], cancellationToken: CancellationToken.None).ConfigureAwait(false);
                created.ThrowIfAnyFailed();
            }
            else if (await catalog.ReadNamespaceBirthObservationAsync(operationId, token).ConfigureAwait(false) is null)
                throw new InvalidOperationException("A started source publication requires original native outcome recovery before retry.");
            var observed = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, operationId, item, router, CancellationToken.None).ConfigureAwait(false);
            var prior = await catalog.ReadLatestNamespaceBirthProtectionAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, prior?.ProtectionId ?? Guid.NewGuid(),
                operationId, item, parent, router, CancellationToken.None).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(CancellationToken.None).ConfigureAwait(false));
            var member = new MirrorPulseNamespaceMemberRegistration(location.CurrentPositionOperationId is null ? 1 : 2, operationId,
                parentMemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, operationId, protection.ProtectionId, birth.RootId, path,
                observed.LocalObject, directory, observed.OwnerSid, DateTimeOffset.UtcNow)
            { FirstParentPositionOperationId = location.CurrentPositionOperationId };
            result = await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }
}
