using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
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
