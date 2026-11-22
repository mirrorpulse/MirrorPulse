using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseNamespaceProtectionProof(RootId? RootId, string RelativePath, bool IsDirectory,
    string OwnerSid, string RoleSid, MirrorPulseNamespacePermissionVerification Verification, string ExpectedDacl,
    Guid PermissionOperationId, MirrorPulseNamespaceBornParent? BornParent, MirrorPulseNamespaceBirthObservation? Observation)
{
    public Guid ProtectionId => BornParent?.ProtectionId ?? PermissionOperationId;
    public MirrorPulseNamespaceMemberIdentity? CurrentIdentity { get; init; }
}

/// <summary>Resolves current product protection without substituting it for fresh native inspection.</summary>
internal static class MirrorPulseNamespaceProtectionProofReader
{
    internal static async Task<MirrorPulseNamespaceProtectionProof> ReadOriginalAsync(MirrorPulseProductCatalog catalog,
        Guid evidenceId, CancellationToken token)
    {
        var original = await catalog.ReadNamespacePermissionBaselineAsync(evidenceId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original permission evidence is missing.");
        var protection = await catalog.ReadLatestNamespacePermissionChangeAsync(evidenceId, token).ConfigureAwait(false);
        if (protection is not { Phase: MirrorPulseNamespacePermissionPhase.Verified, Verification: { } verification } ||
            protection.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
            throw new InvalidOperationException("The original object requires current verified protection.");
        return new(protection.Intent.RootId, protection.Intent.RelativePath, original.IsDirectory, original.OwnerSid,
            protection.Intent.RoleSid, verification, protection.Intent.TargetDacl, protection.Intent.OperationId, null, null);
    }

    internal static async Task<MirrorPulseNamespaceProtectionProof> ReadBirthAsync(MirrorPulseProductCatalog catalog,
        Guid birthId, CancellationToken token)
    {
        var birth = await catalog.ReadNamespaceBirthAsync(birthId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original birth admission is missing.");
        var original = await catalog.ReadNamespaceBirthObservationAsync(birthId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original native birth observation is missing.");
        var protection = await catalog.ReadLatestNamespaceBirthProtectionAsync(birthId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The born object requires verified protection.");
        return new(birth.RootId, protection.Placement?.RelativePath ?? birth.RelativePath, birth.IsDirectory, original.OwnerSid, protection.RoleSid,
            protection.Verification, protection.Verification.Dacl, Guid.Empty,
            new(birth.OperationId, protection.ProtectionId), original);
    }

    internal static async Task<MirrorPulseNamespaceProtectionProof> ReadMemberAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, CancellationToken token)
    {
        var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, token).ConfigureAwait(false);
        if (snapshot is not null)
        {
            var context = snapshot.Context;
            if (context.Member != member)
                throw new InvalidDataException("Current protection cannot adopt different member provenance.");
            return FromSnapshot(snapshot);
        }
        // Registration validates an unpublished candidate using its separate original proofs.
        MirrorPulseNamespaceProtectionProof proof = await (member.Origin switch
        {
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal => ReadOriginalAsync(catalog, member.OriginId, token),
            MirrorPulseNamespaceMemberOrigin.ProtectedBirth => ReadBirthAsync(catalog, member.OriginId, token),
            _ => throw new ArgumentException("The member requires one supported provenance origin.", nameof(member)),
        }).ConfigureAwait(false);
        return proof;
    }

    internal static MirrorPulseNamespaceProtectionProof FromSnapshot(MirrorPulseNamespaceMemberProtectionSnapshot snapshot)
    {
        var context = snapshot.Context;
        var member = context.Member;
        return new(member.RootId, context.Location.RelativePath, member.IsDirectory, member.OwnerSid,
            context.Protection.RoleSid, context.Verification, snapshot.ExpectedDacl, snapshot.PermissionOperationId,
            snapshot.BornParent, snapshot.Observation)
        { CurrentIdentity = snapshot.Identity };
    }
}
