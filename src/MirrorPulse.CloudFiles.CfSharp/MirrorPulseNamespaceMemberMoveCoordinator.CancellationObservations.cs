using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    // Classify only an already rejected immutable read. No new inspection, native
    // operation, catalog read, descriptor normalization or admission occurs here.
    private static InvalidDataException ObserveCancellationObjectFailure(InvalidDataException original,
        MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot, MirrorPulseNamespaceProtectionProof proof,
        MirrorPulseLocalFileBinding binding, string owner, RootId? root, string path, bool directory)
    {
        var stage = ClassifyCancellationObjectFailure(facts, snapshot, proof, binding, owner, root, path, directory);
        MirrorPulseRootLabelCancellationFailureDiagnostics.MarkStage(original, stage);
        return original;
    }

    private static MirrorPulseRootLabelCancellationStage ClassifyCancellationObjectFailure(MirrorPulseNamespacePermissionObject facts,
        CloudItemSnapshot snapshot, MirrorPulseNamespaceProtectionProof proof, MirrorPulseLocalFileBinding binding,
        string owner, RootId? root, string path, bool directory)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } actual)
            return MirrorPulseRootLabelCancellationStage.ObjectSnapshot;
        if (facts.LocalObject != new MirrorPulseLocalFileBinding(actual.VolumeSerialNumber, actual.SyncRootFileId, actual.LocalFileId))
            return MirrorPulseRootLabelCancellationStage.ObjectNativeBinding;
        if (facts.LocalObject != binding || facts.LocalObject != proof.Verification.LocalObject)
            return MirrorPulseRootLabelCancellationStage.ObjectRetainedBinding;
        if (facts.OwnerSid != owner || proof.OwnerSid != owner) return MirrorPulseRootLabelCancellationStage.ObjectOwner;
        if (facts.RootId != root || proof.RootId != root) return MirrorPulseRootLabelCancellationStage.ObjectRoot;
        if (facts.RelativePath != path || proof.RelativePath != path) return MirrorPulseRootLabelCancellationStage.ObjectPath;
        if (facts.IsDirectory != directory || proof.IsDirectory != directory || (snapshot.Kind == CloudItemKind.Directory) != directory)
            return MirrorPulseRootLabelCancellationStage.ObjectKind;
        if (facts.LinkCount != 1) return MirrorPulseRootLabelCancellationStage.ObjectLinkCount;
        if (facts.Dacl != proof.Verification.Dacl) return MirrorPulseRootLabelCancellationStage.ObjectRetainedDescriptor;
        // The unchanged original compound guard already rejected this read. If
        // every earlier predicate matched, its final descriptor-policy check failed.
        return MirrorPulseRootLabelCancellationStage.ObjectDescriptorPolicy;
    }
}
