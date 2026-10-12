using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseNamespaceProtectionProof(RootId? RootId, string RelativePath, bool IsDirectory,
    string OwnerSid, string RoleSid, MirrorPulseNamespacePermissionVerification Verification, string ExpectedDacl,
    Guid PermissionOperationId, MirrorPulseNamespaceBornParent? BornParent, MirrorPulseNamespaceBirthObservation? Observation)
{
    public Guid ProtectionId => BornParent?.ProtectionId ?? PermissionOperationId;
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
        return new(birth.RootId, birth.RelativePath, birth.IsDirectory, original.OwnerSid, protection.RoleSid,
            protection.Verification, protection.Verification.Dacl, Guid.Empty,
            new(birth.OperationId, protection.ProtectionId), original);
    }

    internal static async Task<MirrorPulseNamespaceProtectionProof> ReadMemberAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, CancellationToken token)
    {
        MirrorPulseNamespaceProtectionProof proof = await (member.Origin switch
        {
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal => ReadOriginalAsync(catalog, member.OriginId, token),
            MirrorPulseNamespaceMemberOrigin.ProtectedBirth => ReadBirthAsync(catalog, member.OriginId, token),
            _ => throw new ArgumentException("The member requires one supported provenance origin.", nameof(member)),
        }).ConfigureAwait(false);
        var registered = await catalog.ReadNamespaceMemberAsync(member.MemberId, token).ConfigureAwait(false);
        // Native registration also uses this reader before the member has been published.
        if (registered is null) return proof;
        if (registered != member)
            throw new InvalidDataException("Current protection cannot adopt different member provenance.");
        var epoch = await catalog.ReadNamespaceMemberProtectionEpochAsync(member.MemberId, token).ConfigureAwait(false);
        if (epoch.ProtectionId != proof.ProtectionId || epoch.RoleSid != proof.RoleSid || epoch.VerifiedAt != proof.Verification.ObservedAt)
            throw new InvalidDataException("Current protection must follow the member's retained permission references.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        // A completed product move selects the current position. Neither an official path
        // observation nor a pending native move may rewrite the historical ACL or birth.
        return proof with { RelativePath = location.RelativePath };
    }
}
