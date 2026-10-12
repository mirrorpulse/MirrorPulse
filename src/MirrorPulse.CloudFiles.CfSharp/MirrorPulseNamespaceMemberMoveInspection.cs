using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Checks observed native facts without promoting historical observations to authority.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal static class MirrorPulseNamespaceMemberMoveInspection
{
    internal static void RequireChild(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceProtectionProof proof, string path, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        RequireObject(facts, snapshot, proof, path, intent);
        if (facts.IsDirectory || !snapshot.IsPlaceholder ||
            snapshot.ItemId is { } projectedItem && projectedItem != intent.ItemId ||
            snapshot.RemoteId is { } projectedRemote && projectedRemote != intent.RemoteId ||
            facts.LocalObject != intent.LocalObject || proof.ProtectionId != intent.ProtectionId || snapshot.PlaceholderIdentity.IsEmpty)
            throw new InvalidDataException("The actual moved file lost its original binding or public identity.");
        var identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        if (identity.ItemId != intent.ItemId || identity.RemoteId != intent.RemoteId)
            throw new InvalidDataException("The raw native identity disagrees with the admitted move object.");
    }

    internal static void RequireParent(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceProtectionProof proof, MirrorPulseNamespaceMoveEndpoint endpoint, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        RequireObject(facts, snapshot, proof, endpoint.RelativePath[..endpoint.RelativePath.LastIndexOf('/')], intent);
        if (!facts.IsDirectory || facts.LocalObject != endpoint.ParentLocalObject || proof.ProtectionId != endpoint.ParentProtectionId)
            throw new InvalidDataException("Move execution requires the actual admitted direct parent.");
        if (proof.Observation is { } original)
        {
            if (!snapshot.IsPlaceholder || snapshot.PlaceholderIdentity.IsEmpty)
                throw new InvalidDataException("The actual born parent lost its original public identity.");
            var identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
            if (identity.ItemId != original.ItemId || identity.RemoteId != original.RemoteId ||
                snapshot.ItemId is { } projectedItem && projectedItem != original.ItemId ||
                snapshot.RemoteId is { } projectedRemote && projectedRemote != original.RemoteId)
                throw new InvalidDataException("The actual born parent disagrees with its original public identity.");
        }
    }

    private static void RequireObject(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceProtectionProof proof, string path, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != proof.Verification.LocalObject || facts.OwnerSid != intent.OwnerSid || facts.OwnerSid != proof.Verification.OwnerSid ||
            facts.RootId != intent.RootId || facts.RootId != proof.RootId || facts.RelativePath != path || facts.IsDirectory != proof.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != facts.IsDirectory || facts.LinkCount != 1 ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("The actual namespace object lost its admitted location, binding, owner or protected descriptor.");
    }
}
