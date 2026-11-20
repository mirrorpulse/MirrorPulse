using System.Runtime.Versioning;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Replays only original pending permission intents before guarded unsealed cancellation.</summary>
/// <remarks>The caller owns root drain and instance/library admission. No source work,
/// role rotation, initialization completion, Label cancellation or readiness is performed.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRootLabelPendingPermissionRecoveryCoordinator(CloudFileSystem fileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, MirrorPulseNamespacePermissionCoordinator permissions)
{
    internal ValueTask<int> ReplayAsync(Guid operationId, CancellationToken token) =>
        actor.RunAsync(stop => ReplayAdmittedAsync(operationId, stop), token);

    private async ValueTask<int> ReplayAdmittedAsync(Guid operationId, CancellationToken token)
    {
        actor.AssertAccess();
        // A completed original is history only; do not reopen current native work.
        if (await catalog.ReadUnsealedRootLabelCancellationAsync(operationId, token).ConfigureAwait(false) is not null) return 0;
        var plan = await catalog.ReadRootLabelPendingPermissionRecoveryAsync(operationId, token).ConfigureAwait(false);
        if (plan is null || plan.PendingOriginals.Count == 0) return 0;
        int replayed = 0;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            foreach (var retained in plan.PendingOriginals)
            {
                token.ThrowIfCancellationRequested();
                if (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token).ConfigureAwait(false) != plan.Owner)
                    throw new InvalidOperationException("Pending permission replay cannot replace the original cancellation initializer.");
                var lineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(plan.Owner.InitializationOperationId,
                    retained.Member.MemberId, token).ConfigureAwait(false);
                var current = lineage[0];
                if (!SamePayload(current, retained) || current.Original is null || current.Permission is not { } permission ||
                    permission.Phase is MirrorPulseNamespacePermissionPhase.Verified or MirrorPulseNamespacePermissionPhase.RecoveryRequired ||
                    current.Member.OwnerSid != execution.OwnerSid.Value || lineage.Count < 2)
                    throw new InvalidOperationException("Pending cancellation recovery requires the exact unresolved original member and permission intent.");
                var root = (await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots
                    .Single(item => item.RootId == current.Member.RootId);
                if (root.State is RootRegistrationState.Active or RootRegistrationState.Disabled)
                    router.RefreshLocalCommandLifecycleRegistration(root);
                if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) ||
                    router.FindRegistration(root.RootId) != root)
                    throw new InvalidOperationException("Pending permission replay requires the unchanged current local root route.");
                var parent = lineage[1];
                CloudDirectory parentItem = parent.Location.RelativePath.Length == 0
                    ? fileSystem.Root : fileSystem.GetDirectory(parent.Location.RelativePath);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, token).ConfigureAwait(false);
                var beforeParent = await parentGuard.InspectAsync(token).ConfigureAwait(false);
                RequireObject(parent, beforeParent);
                string parentDacl = parent.Permission is { Phase: MirrorPulseNamespacePermissionPhase.Verified, Verification: { } verified }
                    ? verified.Dacl : parent.BirthProtection?.Verification.Dacl
                        ?? throw new InvalidOperationException("Pending permission replay requires its direct parent to settle first.");
                if (parentDacl != beforeParent.Dacl)
                    throw new InvalidDataException("Pending permission replay cannot adopt a changed parent descriptor.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await parentItem.InspectAsync(token).ConfigureAwait(false),
                    parent.Identity, requirePlaceholder: parent.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
                CloudItem item = current.Member.IsDirectory ? fileSystem.GetDirectory(current.Location.RelativePath)
                    : fileSystem.GetFile(current.Location.RelativePath);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
                RequireObject(current, await guard.InspectAsync(token).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await item.InspectAsync(token).ConfigureAwait(false),
                    current.Identity, requirePlaceholder: !current.Member.IsDirectory);
                if (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token).ConfigureAwait(false) != plan.Owner)
                    throw new InvalidOperationException("The original initializer changed before pending permission application.");
                var result = await permissions.ApplyProtectedAsync(permission.Intent.OperationId, item, router, cancellationToken: token).ConfigureAwait(false);
                if (result.Permission?.Outcome is not (MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified) ||
                    result.Receipt is not { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true })
                    throw new IOException("The original pending permission did not complete and drain its protected native work.");
                var afterLineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(plan.Owner.InitializationOperationId,
                    current.Member.MemberId, token).ConfigureAwait(false);
                var after = afterLineage[0];
                if (after.Member != current.Member || after.Location != current.Location || after.Identity != current.Identity ||
                    after.Original != current.Original || after.Permission is not { Phase: MirrorPulseNamespacePermissionPhase.Verified, Verification: { } readback } settled ||
                    !SamePayload(settled.Intent, permission.Intent) || !SamePayload(afterLineage[1], parent) ||
                    await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token).ConfigureAwait(false) != plan.Owner)
                    throw new InvalidOperationException("Pending permission replay must retain its original intent, initializer, identity and committed parent position.");
                var facts = await guard.InspectAsync(token).ConfigureAwait(false); RequireObject(after, facts);
                if (facts.Dacl != readback.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(permission.Intent.TargetDacl, facts.Dacl))
                    throw new InvalidDataException("Pending permission replay lost its independent final descriptor readback.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await item.InspectAsync(token).ConfigureAwait(false),
                    after.Identity, requirePlaceholder: !after.Member.IsDirectory);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeParent, await parentGuard.InspectAsync(token).ConfigureAwait(false));
                replayed++;
            }
        }).ConfigureAwait(false);
        return replayed;
    }

    private static bool SamePayload<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));

    private static void RequireObject(MirrorPulseNamespaceMemberRecoveryContext context, MirrorPulseNamespacePermissionObject facts)
    {
        if (facts.LocalObject != context.Member.LocalObject || facts.OwnerSid != context.Member.OwnerSid ||
            facts.RootId != context.Member.RootId || facts.RelativePath != context.Location.RelativePath ||
            facts.IsDirectory != context.Member.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("Pending permission replay lost the original object binding, owner, current path, kind or single-link constraint.");
    }
}
