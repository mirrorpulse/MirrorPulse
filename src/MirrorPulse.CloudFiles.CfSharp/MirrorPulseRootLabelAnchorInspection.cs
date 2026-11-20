using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Checks only the retained physical sync-root anchor of an original linked Label operation.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal static class MirrorPulseRootLabelAnchorInspection
{
    internal static MirrorPulseNamespaceProtectionProof RequireProof(MirrorPulseNamespaceMemberProtectionSnapshot snapshot,
        MirrorPulseNamespaceMemberMoveIntent intent)
    {
        var context = snapshot.Context;
        var member = context.Member;
        var proof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(snapshot);
        if (intent.Version != 3 || intent.RemoteEntry is not null || intent.DirectoryMoveEvidence is null ||
            intent.Source.ParentMemberId != intent.Target.ParentMemberId || intent.Source.ParentLocalObject != intent.Target.ParentLocalObject ||
            intent.Source.ParentProtectionId != intent.Target.ParentProtectionId ||
            intent.Source.ParentPositionOperationId != intent.Target.ParentPositionOperationId ||
            member.MemberId != intent.Source.ParentMemberId || member.ParentMemberId is not null || member.RootId is not null ||
            !member.IsDirectory || member.RelativePath.Length != 0 || context.Location.MemberId != member.MemberId ||
            context.Location.ParentMemberId is not null || context.Location.RelativePath.Length != 0 ||
            context.Location.CurrentPositionOperationId != intent.Source.ParentPositionOperationId ||
            member.LocalObject != intent.Source.ParentLocalObject || member.OwnerSid != intent.OwnerSid ||
            member.LocalObject.LocalFileId != intent.LocalObject.SyncRootFileId ||
            member.LocalObject.SyncRootFileId != intent.LocalObject.SyncRootFileId ||
            member.LocalObject.VolumeSerialNumber != intent.LocalObject.VolumeSerialNumber ||
            member.LocalObject == intent.LocalObject || proof.RootId is not null || proof.RelativePath.Length != 0 || !proof.IsDirectory ||
            context.Protection.ProtectionId != intent.Source.ParentProtectionId || context.Protection.RoleSid != intent.RoleSid ||
            context.Protection.VerifiedAt != proof.Verification.ObservedAt ||
            proof.OwnerSid != intent.OwnerSid || proof.RoleSid != intent.RoleSid ||
            proof.ProtectionId != intent.Source.ParentProtectionId || proof.Verification.LocalObject != member.LocalObject ||
            proof.Verification.OwnerSid != intent.OwnerSid || proof.Verification.ObservedAt > intent.PreparedAt ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, proof.Verification.Dacl))
            throw new InvalidOperationException("Root Label preparation requires its current original protected sync-root anchor.");
        return proof;
    }

    internal static void RequireNative(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceProtectionProof proof, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        if (intent.Version != 3 || !snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.Directory ||
            snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != proof.Verification.LocalObject || facts.LocalObject != intent.Source.ParentLocalObject ||
            facts.LocalObject != intent.Target.ParentLocalObject || facts.LocalObject.LocalFileId != intent.LocalObject.SyncRootFileId ||
            facts.RootId is not null || proof.RootId is not null || facts.RelativePath.Length != 0 || proof.RelativePath.Length != 0 ||
            !facts.IsDirectory || !proof.IsDirectory || facts.LinkCount != 1 || facts.OwnerSid != intent.OwnerSid || facts.OwnerSid != proof.OwnerSid ||
            proof.RoleSid != intent.RoleSid || facts.Dacl != proof.Verification.Dacl ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("Root Label preparation lost its actual sync-root anchor, owner or protected descriptor.");
    }
}
