using System.Runtime.Versioning;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Settles original permission and born work before the existing guarded cancellation.</summary>
/// <remarks>The Host caller owns root drain and instance/library admission. This does not
/// complete initialization, cancel Label, release fences, grant readiness or access a backend.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRootLabelCancellationRecoveryCoordinator(CloudFileSystem fileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, IMirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    internal Func<MirrorPulseNamespaceBornMemberRecoveryCheckpoint, CancellationToken, ValueTask>? BornCheckpoint { get; init; }

    internal async ValueTask ReplayAsync(Guid operationId, CancellationToken cancellationToken)
    {
        // Permission replay owns its actor turn and execution scope. Do not nest either.
        await new MirrorPulseRootLabelPendingPermissionRecoveryCoordinator(fileSystem, catalog, router, execution, actor, permissions)
            .ReplayAsync(operationId, cancellationToken).ConfigureAwait(false);
        await actor.RunAsync(token => ReplayBornAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReplayBornAdmittedAsync(Guid operationId, CancellationToken token)
    {
        actor.AssertAccess();
        if (await catalog.ReadUnsealedRootLabelCancellationAsync(operationId, token).ConfigureAwait(false) is not null) return 0;
        var plan = await catalog.ReadRootLabelPendingPermissionRecoveryAsync(operationId, token).ConfigureAwait(false);
        if (plan is null) return 0;
        if (plan.PendingOriginals.Count != 0)
            throw new InvalidOperationException("Original permission intents must settle before born cancellation recovery.");
        var rootMember = await catalog.ReadNamespaceMemberAsync(plan.RootMemberId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation recovery lost its original registered root.");
        var root = (await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(item => item.RootId == rootMember.RootId);
        if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("Cancellation recovery requires its original local lifecycle.");
        router.RefreshLocalCommandLifecycleRegistration(root);
        var owner = new MirrorPulseNamespaceBornRootLabelRecoveryOwner(operationId, plan.Owner);
        var recovery = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
        { Checkpoint = BornCheckpoint };
        int settled = 0;
        foreach (Guid memberId in plan.BornMembers)
        {
            token.ThrowIfCancellationRequested();
            if (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token).ConfigureAwait(false) != plan.Owner)
                throw new InvalidOperationException("Born cancellation recovery cannot replace its original initializer.");
            var context = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(plan.Owner.InitializationOperationId, memberId, token).ConfigureAwait(false))[0];
            if (context.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || context.Member.RootId != root.RootId ||
                context.Member.OwnerSid != execution.OwnerSid.Value || context.BirthProtection is null)
                throw new InvalidDataException("Cancellation recovery lost a selected born member's original scope or provenance.");
            var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, token).ConfigureAwait(false);
            var latest = history.Count == 0 ? null : history[^1];
            bool unfinished = latest is not null && await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(latest.ActionId, token).ConfigureAwait(false) is null;
            if (unfinished && (latest!.InitializationOperationId != plan.Owner.InitializationOperationId ||
                latest.RootLabelRecoveryOwner is { } previousOwner && previousOwner != owner))
                throw new InvalidOperationException("Cancellation cannot adopt another original born recovery owner.");
            var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(context.Location.ParentMemberId!.Value, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Born cancellation recovery requires its direct parent to settle first.");
            // A new Host role alone does not invalidate an older native verification.
            // An unfinished action still has to settle even when its descriptor matches.
            bool retainedProtection = await HasRetainedNativeProtectionAsync(owner, context, parent, token).ConfigureAwait(false);
            if (!unfinished && retainedProtection) continue;
            Guid actionId = unfinished && latest!.RootLabelRecoveryOwner == owner &&
                SamePayload(latest.Child, context) && SamePayload(latest.Parent, parent) ? latest.ActionId : Guid.NewGuid();
            var result = await recovery.RecoverRootLabelAdmittedAsync(plan.Owner.InitializationOperationId, actionId, memberId, owner, token).ConfigureAwait(false);
            if (result.Completion is null || !result.IsHistoricalReplay &&
                result.Receipt is not { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true })
                throw new IOException("The original born cancellation recovery did not complete and drain its protected work.");
            settled++;
        }
        if (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token).ConfigureAwait(false) != plan.Owner)
            throw new InvalidOperationException("The original cancellation initializer changed after born recovery.");
        return settled;
    }

    private async Task<bool> HasRetainedNativeProtectionAsync(MirrorPulseNamespaceBornRootLabelRecoveryOwner owner,
        MirrorPulseNamespaceMemberRecoveryContext child, MirrorPulseNamespaceMemberProtectionSnapshot parent, CancellationToken token)
    {
        actor.AssertAccess();
        bool matches = false;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            if (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(owner.RootLabelOperationId, token).ConfigureAwait(false) != owner.Initialization)
                throw new InvalidOperationException("Native cancellation inspection lost its original initialization witness.");
            CloudItem item = child.Member.IsDirectory ? fileSystem.GetDirectory(child.Location.RelativePath) : fileSystem.GetFile(child.Location.RelativePath);
            var parentItem = parent.Context.Location.RelativePath.Length == 0 ? fileSystem.Root : fileSystem.GetDirectory(parent.Context.Location.RelativePath);
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, token).ConfigureAwait(false);
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
            var beforeParent = await parentGuard.InspectAsync(token).ConfigureAwait(false);
            RequireObject(parent.Context.Member, parent.Context.Location, beforeParent, await parentItem.InspectAsync(token).ConfigureAwait(false), parent.Identity);
            if (beforeParent.Dacl != parent.Context.Verification.Dacl)
                throw new InvalidDataException("Native cancellation inspection requires its settled direct parent's verified descriptor.");
            var before = await guard.InspectAsync(token).ConfigureAwait(false);
            RequireObject(child.Member, child.Location, before, await item.InspectAsync(token).ConfigureAwait(false), child.Identity);
            var after = await guard.InspectAsync(token).ConfigureAwait(false);
            RequireObject(child.Member, child.Location, after, await item.InspectAsync(token).ConfigureAwait(false), child.Identity);
            if (after.Dacl != before.Dacl)
                throw new InvalidDataException("The selected born descriptor changed during cancellation inspection.");
            var afterParent = await parentGuard.InspectAsync(token).ConfigureAwait(false);
            RequireObject(parent.Context.Member, parent.Context.Location, afterParent, await parentItem.InspectAsync(token).ConfigureAwait(false), parent.Identity);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(beforeParent, afterParent);
            var current = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(owner.Initialization.InitializationOperationId,
                child.Member.MemberId, token).ConfigureAwait(false))[0];
            if (!SamePayload(current, child) || !SamePayload(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent.Context.Member.MemberId, token).ConfigureAwait(false), parent) ||
                await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(owner.RootLabelOperationId, token).ConfigureAwait(false) != owner.Initialization)
                throw new InvalidOperationException("Native cancellation inspection lost its admitted member, parent or owner.");
            matches = before.Dacl == child.BirthProtection!.Verification.Dacl &&
                MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(child.BirthProtection.Verification.Dacl, before.Dacl);
        }).ConfigureAwait(false);
        return matches;
    }

    private static void RequireObject(MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceMemberLocation location,
        MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot, MirrorPulseNamespaceMemberIdentity identity)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != member.LocalObject || facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.OwnerSid != member.OwnerSid || facts.RootId != member.RootId || facts.RelativePath != location.RelativePath ||
            facts.IsDirectory != member.IsDirectory || (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("Native cancellation inspection lost its retained binding, owner, path, kind or single-link constraint.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, identity,
            requirePlaceholder: member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth || !member.IsDirectory);
    }

    private static bool SamePayload<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));
}
