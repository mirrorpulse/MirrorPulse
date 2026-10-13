using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Registers a protected member only after fresh child and direct-parent native checks.</summary>
/// <remarks>
/// The caller retains the supplied shared namespace actor and root/ancestor lifetime guards. Metadata
/// handles alone do not freeze names. This boundary writes no ACL or content, performs no
/// conversion or source operation, and supplies no tree-readiness or upload acceptance proof.
/// First-location provenance remains immutable; relocation and retirement have separate lifecycles.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespaceMemberRegistrar
{
    public static async Task<MirrorPulseNamespaceMemberRegistration> RegisterAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, CloudItem item, CloudDirectory? parent,
        MirrorPulseRootRouter router, MirrorPulseNamespaceActor namespaceActor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(namespaceActor);
        cancellationToken.ThrowIfCancellationRequested();
        namespaceActor.AssertAccess();
        var replay = await catalog.ReadNamespaceMemberAsync(member.MemberId, cancellationToken).ConfigureAwait(false);
        if (replay is not null && replay != member)
            throw new InvalidOperationException("Native registration cannot replace immutable member provenance.");
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, cancellationToken).ConfigureAwait(false);
        if (replay is null && proof.ProtectionId != member.ProtectionId)
            throw new InvalidOperationException("New native registration requires the latest retained protection epoch.");
        RequireCaller(proof);
        if (member.ParentMemberId is { } parentId)
        {
            if (parent is null || item.Parent is null || !string.Equals(item.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native registration requires the actual direct parent directory.");
            var parentMember = await catalog.ReadNamespaceMemberAsync(parentId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The direct parent has no retained member provenance.");
            var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, cancellationToken).ConfigureAwait(false);
            RequireCaller(parentProof);
            if (!parentProof.IsDirectory || parentProof.RoleSid != proof.RoleSid)
                throw new InvalidDataException("The direct parent requires the same current namespace role.");
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, cancellationToken).ConfigureAwait(false);
            var before = await InspectAsync(catalog, parentMember, parentProof, parent, parentLease, cancellationToken).ConfigureAwait(false);
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, cancellationToken).ConfigureAwait(false);
            _ = await InspectAsync(catalog, member, proof, item, lease, cancellationToken).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before,
                await parentLease.InspectAsync(cancellationToken).ConfigureAwait(false));
            await RequireUnchangedProofAsync(catalog, parentMember, parentProof, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedProofAsync(catalog, member, proof, cancellationToken).ConfigureAwait(false);
            namespaceActor.AssertAccess();
            return await catalog.RegisterNamespaceMemberAsync(member, cancellationToken).ConfigureAwait(false);
        }
        if (parent is not null || item.Parent is not null || !router.IsSyncRoot(item.FullPath))
            throw new InvalidDataException("Only the actual sync-root anchor can register without a direct parent.");
        await using var rootLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, cancellationToken).ConfigureAwait(false);
        _ = await InspectAsync(catalog, member, proof, item, rootLease, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedProofAsync(catalog, member, proof, cancellationToken).ConfigureAwait(false);
        namespaceActor.AssertAccess();
        return await catalog.RegisterNamespaceMemberAsync(member, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireCaller(MirrorPulseNamespaceProtectionProof proof)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != proof.OwnerSid || !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(proof.RoleSid)))
            throw new UnauthorizedAccessException("Native member registration requires the original owner and current namespace role.");
    }

    private static async Task<MirrorPulseNamespacePermissionObject> InspectAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceProtectionProof proof, CloudItem item,
        MirrorPulseWindowsNamespacePermissionLease lease, CancellationToken token)
    {
        var facts = await lease.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        var registered = await catalog.ReadNamespaceMemberAsync(member.MemberId, token).ConfigureAwait(false);
        if (registered is not null && registered != member)
            throw new InvalidDataException("Native registration lost its immutable member provenance.");
        string path = registered is null ? member.RelativePath :
            (await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false)).RelativePath;
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != member.LocalObject || facts.LocalObject != proof.Verification.LocalObject ||
            facts.OwnerSid != member.OwnerSid || facts.OwnerSid != proof.OwnerSid || facts.OwnerSid != proof.Verification.OwnerSid ||
            facts.RootId != member.RootId || facts.RootId != proof.RootId || facts.RelativePath != path || facts.RelativePath != proof.RelativePath ||
            facts.IsDirectory != member.IsDirectory || facts.IsDirectory != proof.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory || facts.LinkCount != 1 || facts.Dacl != proof.Verification.Dacl ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("Native member registration requires the original object and its exact current protected location and descriptor.");
        if (proof.CurrentIdentity is { } identity)
            MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, identity,
                member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
        else if (proof.Observation is { } observation)
        {
            if (!snapshot.IsPlaceholder || snapshot.ItemId != observation.ItemId || snapshot.RemoteId != observation.RemoteId)
                throw new InvalidDataException("The born member lost its original public Cloud Files identity.");
        }
        else
        {
            var originalIdentity = await catalog.ReadNamespacePermissionLocalIdentityAsync(member.OriginId, token).ConfigureAwait(false);
            if (!member.IsDirectory && originalIdentity is null)
                throw new InvalidDataException("The original file member has no retained pre-conversion public identity.");
            if (originalIdentity is not null && (!snapshot.IsPlaceholder || snapshot.ItemId != originalIdentity.ItemId || snapshot.RemoteId != originalIdentity.RemoteId))
                throw new InvalidDataException("The original member lost its retained public Cloud Files identity.");
        }
        return facts;
    }

    private static async Task RequireUnchangedProofAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceProtectionProof before, CancellationToken token)
    {
        var after = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        if (after != before) throw new InvalidOperationException("Protection changed during native member registration.");
    }
}
