using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Verifies inherited protection of a registered born member under a pending recovery owner.</summary>
/// <remarks>
/// This consumes actual current-parent protection after its recovery. No conversion, ACL write,
/// hydration, first-observation replacement or root admission is performed. Ordinary files
/// that require conversion must use the separate before-write recovery path.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceBornMemberReadbackCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    IMirrorPulseInstanceScheduler scheduler)
{
    public async ValueTask<MirrorPulseNamespaceBirthProtection> VerifyAsync(Guid initializationOperationId, Guid protectionId,
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (protectionId == Guid.Empty) throw new ArgumentException("A born protection epoch ID is required.", nameof(protectionId));
        var admitted = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, cancellationToken).ConfigureAwait(false))[0];
        if (admitted.BirthProtection is null) throw new ArgumentException("Born read-back requires registered birth provenance.", nameof(memberId));
        var registration = router.Registrations.Single(root => root.RootId == admitted.Member.RootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            MirrorPulseNamespaceBirthProtection result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var context = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false))[0];
                if (context.Member != admitted.Member || context.Location != admitted.Location || context.Identity != admitted.Identity)
                    throw new InvalidDataException("The admitted born member changed before read-back.");
                var current = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
                if (current.RootId != registration.RootId || current.DirectoryName != registration.DirectoryName ||
                    context.Member.OwnerSid != execution.OwnerSid.Value)
                    throw new InvalidDataException("Born read-back lost its current root layout or original owner.");
                var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(context.Location.ParentMemberId!.Value, stop).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The actual born parent has no coherent recovered protection.");
                if (parent.Context.Protection.RoleSid != execution.RoleSid.Value)
                    throw new InvalidOperationException("The actual parent must first recover to the live Host role.");
                CloudItem item = context.Member.IsDirectory ? fileSystem.GetDirectory(context.Location.RelativePath) : fileSystem.GetFile(context.Location.RelativePath);
                var parentItem = parent.Context.Location.RelativePath.Length == 0 ? fileSystem.Root :
                    fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, stop).ConfigureAwait(false);
                result = await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, protectionId, memberId,
                    item, parentItem, router, actor, stop).ConfigureAwait(false);
                // The native verifier records the existing V3 located epoch. Recheck the
                // pending owner after recording; this never substitutes for root readiness.
                var after = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false))[0];
                if (after.Member != context.Member || after.Location != context.Location || after.Identity != context.Identity ||
                    after.BirthProtection != result || result.RoleSid != execution.RoleSid.Value)
                    throw new InvalidDataException("Born read-back changed its retained provenance or current recovery admission.");
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }
}
