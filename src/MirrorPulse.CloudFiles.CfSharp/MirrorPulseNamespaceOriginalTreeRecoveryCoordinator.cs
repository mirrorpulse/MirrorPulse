using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Restores one retained original capture under the Host's shared scheduling boundary.</summary>
/// <remarks>
/// This recovers the immutable original inventory. It does not recapture relocated members or
/// protected births, release their reservations, acknowledge the journal or declare the whole
/// product namespace ready. Added or missing objects still produce an explicit recovery result.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceOriginalTreeRecoveryCoordinator(CloudFileSystem fileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, IMirrorPulseInstanceScheduler scheduler,
    MirrorPulseNamespacePermissionCoordinator permissions)
{
    public async ValueTask<MirrorPulseNamespacePermissionTreeResult> RecoverAsync(Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        if (manifestId == Guid.Empty) throw new ArgumentException("An original permission manifest ID is required.", nameof(manifestId));
        var tree = await catalog.ReadNamespacePermissionTreeAsync(manifestId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original permission capture is missing.");
        if (tree.Phase != MirrorPulseNamespacePermissionTreePhase.Sealed)
            throw new InvalidOperationException("Original tree recovery requires a sealed capture; open captures must first be resumed or cancelled.");
        var registration = router.Registrations.Single(root => root.RootId == tree.Definition.Anchor.Intent.RootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            actor.AssertAccess();
            var retained = await catalog.ReadNamespacePermissionTreeAsync(manifestId, stop).ConfigureAwait(false);
            if (retained != tree) throw new InvalidDataException("The admitted original permission capture changed.");
            var current = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
            if (current.RootId != registration.RootId || current.DirectoryName != tree.Definition.Anchor.Intent.RelativePath)
                return new MirrorPulseNamespacePermissionTreeResult(manifestId, MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired,
                    0, DateTimeOffset.UtcNow, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged);
            MirrorPulseNamespacePermissionTreeResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                await using var lease = await MirrorPulseWindowsNamespacePermissionTreeLease.OpenAsync(
                    fileSystem.GetDirectory(current.DirectoryName), router, stop).ConfigureAwait(false);
                result = await permissions.RestoreTreeAsync(manifestId, lease, execution,
                    baseline => baseline.IsDirectory ? null : router.CreateFileIdentity(current.InstanceId, current.UniquenessKey,
                        "local/" + baseline.EvidenceId.ToString("D")), stop).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }
}
