using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    internal async Task<T> RunAdmittedBornMemberRecoveryAsync<T>(Guid actionId,
        Func<MirrorPulseNamespaceBornMemberRecoveryAction, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var action = await _catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(actionId, cancellationToken).ConfigureAwait(false);
        var admission = await _catalog.ReadNamespaceInitializationStatusAsync(action.InitializationOperationId, cancellationToken).ConfigureAwait(false);
        var root = admission.Intent.Roots.Single(root => root.RootId == action.Child.Member.RootId);
        if (root.BornRootMemberId is { } bornRootId)
        {
            // A born root has no original capture. Retain the same application,
            // capture and disposal fences using its actual registered root scope.
            var binding = action.Child.Member.LocalObject;
            var scope = new PermissionAdmissionScope(binding.VolumeSerialNumber, binding.SyncRootFileId, root.RootId);
            EnterApplication(scope); bool entered = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
                var current = await _catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(actionId, cancellationToken).ConfigureAwait(false);
                var lineage = await _catalog.ReadNamespaceMemberRecoveryLineageAsync(action.InitializationOperationId,
                    action.Child.Member.MemberId, cancellationToken).ConfigureAwait(false);
                var top = lineage.SingleOrDefault(member => member.Member.MemberId == bornRootId);
                var anchor = lineage[^1];
                if (current != action || admission.Completion is not null || admission.Intent.Version is not (2 or 4) ||
                    admission.Intent.RequestedInventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory ||
                    root.ManifestId != Guid.Empty || top is null || top.Member.RootId != root.RootId || !top.Member.IsDirectory ||
                    top.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth ||
                    top.Location.ParentMemberId != anchor.Member.MemberId || anchor.Member.RootId is not null ||
                    anchor.Member.ParentMemberId is not null || anchor.Member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal ||
                    top.Member.LocalObject.VolumeSerialNumber != binding.VolumeSerialNumber ||
                    top.Member.LocalObject.SyncRootFileId != binding.SyncRootFileId)
                    throw new InvalidDataException("Born recovery lost its admitted registered root or actual sync-root lineage.");
                cancellationToken.ThrowIfCancellationRequested();
                return await work(current, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (entered) _gate.Release();
                ExitApplication(scope);
            }
        }
        Guid manifest = root.ManifestId;
        return await RunAdmittedTreeWorkAsync(manifest, async tree =>
        {
            var current = await _catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(actionId, cancellationToken).ConfigureAwait(false);
            if (current != action || tree.Definition.Anchor.Intent.RootId != action.Child.Member.RootId ||
                tree.Definition.Anchor.Baseline.LocalObject.VolumeSerialNumber != action.Child.Member.LocalObject.VolumeSerialNumber ||
                tree.Definition.Anchor.Baseline.LocalObject.SyncRootFileId != action.Child.Member.LocalObject.SyncRootFileId)
                throw new InvalidDataException("Born recovery lost its current action or sealed original root scope.");
            return await work(current, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // A completed move can leave a verified historical permission intent at its old path.
    // Admit current-member recovery through the existing sealed-root application owner,
    // without pretending that historical intent authorizes application at the new path.
    internal async Task<T> RunAdmittedOriginalMemberRecoveryAsync<T>(Guid initializationOperationId, Guid memberId,
        Func<MirrorPulseNamespaceMemberRecoveryContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var admission = await _catalog.ReadNamespaceInitializationStatusAsync(initializationOperationId, cancellationToken).ConfigureAwait(false);
        var admitted = (await _catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, cancellationToken).ConfigureAwait(false))[0];
        if (admitted.Original is null) throw new InvalidOperationException("Current original recovery requires captured provenance.");
        Guid manifest = admission.Intent.Roots.Single(root => root.RootId == admitted.Member.RootId).ManifestId;
        return await RunAdmittedTreeWorkAsync(manifest, async tree =>
        {
            var current = (await _catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, cancellationToken).ConfigureAwait(false))[0];
            if (current != admitted || tree.Definition.Anchor.Intent.RootId != current.Member.RootId ||
                tree.Definition.Anchor.Baseline.LocalObject.VolumeSerialNumber != current.Member.LocalObject.VolumeSerialNumber ||
                tree.Definition.Anchor.Baseline.LocalObject.SyncRootFileId != current.Member.LocalObject.SyncRootFileId)
                throw new InvalidDataException("The current member recovery changed its retained original root admission.");
            return await work(current, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
