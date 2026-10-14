using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Recovers one registered original at its committed current position under a pending root owner.</summary>
/// <remarks>First capture and membership stay immutable. This operation releases no root fence or journal entry.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    public async ValueTask<MirrorPulseProtectedNamespacePermissionResult> RecoverAsync(Guid initializationOperationId, Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var admitted = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, cancellationToken).ConfigureAwait(false))[0];
        if (admitted.Original is null) throw new ArgumentException("Original member recovery requires captured original provenance.", nameof(memberId));
        var registration = router.Registrations.Single(root => root.RootId == admitted.Member.RootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(async stop =>
        {
            MirrorPulseProtectedNamespacePermissionResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var lineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false);
                var context = lineage[0];
                if (context.Member != admitted.Member || context.Location != admitted.Location || context.Identity != admitted.Identity)
                    throw new InvalidDataException("The admitted original member changed before recovery.");
                var current = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
                if (current.RootId != registration.RootId || current.DirectoryName != registration.DirectoryName)
                    throw new InvalidDataException("The admitted recovery root layout changed.");
                if (context.Member.OwnerSid != execution.OwnerSid.Value)
                    throw new InvalidDataException("Recovery requires the original current-user owner.");
                var parent = lineage[1];
                CloudDirectory parentItem = parent.Location.RelativePath.Length == 0 ? fileSystem.Root : fileSystem.GetDirectory(parent.Location.RelativePath);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, stop).ConfigureAwait(false);
                var parentBefore = await parentGuard.InspectAsync(stop).ConfigureAwait(false); RequireOriginalObject(parent, parentBefore);
                if (!MatchesRetainedParent(parent, parentBefore.Dacl))
                    throw new InvalidDataException("The direct parent requires its own retained protection recovery.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await parentItem.InspectAsync(stop).ConfigureAwait(false),
                    parent.Identity, requirePlaceholder: parent.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
                CloudItem item = context.Member.IsDirectory ? fileSystem.GetDirectory(context.Location.RelativePath) : fileSystem.GetFile(context.Location.RelativePath);
                // The metadata lease keeps the actual object and ancestor names stable across
                // intent persistence, same-object protected work and the independent read-back.
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
                var before = await guard.InspectAsync(stop).ConfigureAwait(false);
                RequireOriginalObject(context, before);
                var latest = context.Permission!;
                bool currentRoleScopeCompleted = false;
                if (latest.Phase != MirrorPulseNamespacePermissionPhase.Verified)
                {
                    result = await permissions.ApplyProtectedAsync(latest.Intent.OperationId, item, router, cancellationToken: stop).ConfigureAwait(false);
                    if (!Verified(result)) return;
                    context = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false))[0];
                    latest = context.Permission!;
                    before = await guard.InspectAsync(stop).ConfigureAwait(false); RequireOriginalObject(context, before);
                    currentRoleScopeCompleted = latest.Intent.RoleSid == execution.RoleSid.Value;
                }
                if (before.Dacl != latest.Verification!.Dacl)
                    throw new InvalidDataException("The original object's current descriptor differs from its retained verified protection.");
                if (latest.Intent.RoleSid != execution.RoleSid.Value)
                {
                    var rotation = latest.Intent with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                        RelativePath = context.Location.RelativePath,
                        RoleSid = execution.RoleSid.Value,
                        ExpectedDacl = latest.Verification.Dacl,
                        TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, context.Member.IsDirectory),
                        PreparedAt = DateTimeOffset.UtcNow,
                        Relocation = context.Location.CurrentPositionOperationId is { } position ? new(memberId, position) : null,
                        ReplacementProjection = null
                    };
                    latest = await catalog.PrepareNamespacePermissionChangeAsync(context.Original!, rotation, stop).ConfigureAwait(false);
                }
                // Replaying a pending current-role intent already completed and drained
                // its native scope. Keep that receipt and perform the independent final audit.
                if (!currentRoleScopeCompleted && latest.Intent.RelativePath == context.Location.RelativePath)
                    result = await permissions.ApplyProtectedAsync(latest.Intent.OperationId, item, router, cancellationToken: stop).ConfigureAwait(false);
                else if (!currentRoleScopeCompleted)
                {
                    // Same-role replay after a completed move does not rewrite an old intent's
                    // path or fabricate a later permission epoch. Audit under a fresh scope.
                    MirrorPulseNamespacePermissionResult? identityFailure = null;
                    var receipt = await permissions.RunAdmittedOriginalMemberRecoveryAsync(initializationOperationId, memberId,
                        (retained, permissionToken) =>
                        {
                            if (retained != context) throw new InvalidDataException("Current original recovery changed before its protected scope.");
                            return MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(item, router, context.Member.LocalObject,
                                async (lease, scopeToken) =>
                                {
                                    var observed = await lease.InspectAsync(scopeToken).ConfigureAwait(false); RequireOriginalObject(context, observed);
                                    if (observed.Dacl != latest.Verification!.Dacl)
                                        throw new InvalidDataException("The moved original descriptor changed before its fresh scope audit.");
                                    identityFailure = await permissions.PrepareRetainedLocalIdentityAsync(latest, context.Original!, lease, null, scopeToken).ConfigureAwait(false);
                                }, permissionToken);
                        }, stop).ConfigureAwait(false);
                    result = new(identityFailure ?? (receipt.Outcome == CloudProtectedLocalOperationOutcome.Completed
                        ? new(latest.Intent.OperationId, MirrorPulseNamespacePermissionOutcome.AlreadyVerified)
                        : receipt.Outcome == CloudProtectedLocalOperationOutcome.LocalObjectMismatch
                            ? new(latest.Intent.OperationId, MirrorPulseNamespacePermissionOutcome.RecoveryRequired, MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged)
                            : null), receipt);
                }
                if (!Verified(result)) return;
                var after = await guard.InspectAsync(stop).ConfigureAwait(false); RequireOriginalObject(context, after);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentGuard.InspectAsync(stop).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await parentItem.InspectAsync(stop).ConfigureAwait(false),
                    parent.Identity, requirePlaceholder: parent.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
                if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                    MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, context.Member.IsDirectory), after.Dacl))
                    throw new InvalidDataException("The recovered original does not retain the live Host role policy.");
                var snapshot = await item.InspectAsync(stop).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, context.Identity, requirePlaceholder: !context.Member.IsDirectory);
                var finalLineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false);
                var final = finalLineage[0];
                if (final.Member != context.Member || final.Location != context.Location || final.Identity != context.Identity ||
                    final.Permission?.Phase != MirrorPulseNamespacePermissionPhase.Verified ||
                    final.Permission.Intent.OperationId != latest.Intent.OperationId || final.Permission.Intent.RoleSid != execution.RoleSid.Value ||
                    finalLineage[1] != parent)
                    throw new InvalidDataException("The original member's durable admission changed during recovery.");
            }).ConfigureAwait(false);
            return result;
        }, token), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireOriginalObject(MirrorPulseNamespaceMemberRecoveryContext context, MirrorPulseNamespacePermissionObject facts)
    {
        if (facts.LocalObject != context.Member.LocalObject || facts.OwnerSid != context.Member.OwnerSid ||
            facts.RootId != context.Member.RootId || facts.RelativePath != context.Location.RelativePath ||
            facts.IsDirectory != context.Member.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("Original recovery lost the actual binding, owner, current path, type or single-link constraint.");
    }

    private static bool Verified(MirrorPulseProtectedNamespacePermissionResult result) =>
        result.Permission?.Outcome is MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified &&
        result.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true };

    private static bool MatchesRetainedParent(MirrorPulseNamespaceMemberRecoveryContext parent, string dacl) =>
        parent.Permission is { } change ? change.Phase == MirrorPulseNamespacePermissionPhase.Verified ? change.Verification!.Dacl == dacl :
            change.Intent.ExpectedDacl == dacl || MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(change.Intent.TargetDacl, dacl) :
            parent.BirthProtection!.Verification.Dacl == dacl;
}
