using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceBornMemberRecoveryCheckpoint { AfterActionPrepared, AfterScopeDrained, AfterProtectionReadback, AfterCompletionRecorded, BeforeProtectedCallbackReturns }

public sealed record MirrorPulseNamespaceBornMemberRecoveryResult(Guid ActionId,
    MirrorPulseNamespaceBornMemberRecoveryCompletion? Completion, CloudProtectedLocalOperationResult? Receipt,
    bool IsHistoricalReplay);

/// <summary>Restores registered born objects through before-write actions and public same-object scopes.</summary>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceBornMemberRecoveryCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    IMirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions)
{
    internal Action<string>? Progress { get; init; }
    internal Func<MirrorPulseNamespaceBornMemberRecoveryCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async ValueTask<MirrorPulseNamespaceBornMemberRecoveryResult> RecoverAsync(Guid initializationOperationId,
        Guid actionId, Guid memberId, CancellationToken cancellationToken = default)
    {
        if (actionId == Guid.Empty) throw new ArgumentException("A recovery action ID is required.", nameof(actionId));
        Progress?.Invoke("NamespaceBornRecoveryHistoryBegin");
        var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, cancellationToken).ConfigureAwait(false);
        Progress?.Invoke("NamespaceBornRecoveryHistoryCompleted");
        var retained = history.SingleOrDefault(value => value.ActionId == actionId);
        if (retained is not null)
        {
            if (retained.InitializationOperationId != initializationOperationId) throw new InvalidOperationException("The action belongs to a different initializer.");
            if (await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(actionId, cancellationToken).ConfigureAwait(false) is { } completed)
                return new(actionId, completed, null, true);
        }
        var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The born recovery member is not registered.");
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth) throw new ArgumentException("Born recovery requires birth provenance.", nameof(memberId));
        var registration = router.Registrations.Single(root => root.RootId == member.RootId);
        return await scheduler.RunAsync(registration.InstanceId, token => actor.RunAsync(stop =>
            RecoverAdmittedAsync(initializationOperationId, actionId, memberId, member, registration,
                retained?.RootLabelRecoveryOwner, stop), token), cancellationToken).ConfigureAwait(false);
    }

    // The caller already owns root drain, instance/library admission and this actor.
    // Each recovered object retains its own original protected SDK scope and readback.
    internal async ValueTask<MirrorPulseNamespaceBornMemberRecoveryResult> RecoverRootLabelAdmittedAsync(
        Guid initializationOperationId, Guid actionId, Guid memberId, MirrorPulseNamespaceBornRootLabelRecoveryOwner owner,
        CancellationToken cancellationToken)
    {
        actor.AssertAccess();
        ArgumentNullException.ThrowIfNull(owner);
        if (actionId == Guid.Empty || owner.Initialization.InitializationOperationId != initializationOperationId)
            throw new ArgumentException("Born recovery requires its original action and retained initialization witness.");
        var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The born recovery member is not registered.");
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            throw new ArgumentException("Born recovery requires birth provenance.", nameof(memberId));
        var registration = router.Registrations.Single(root => root.RootId == member.RootId);
        return await RecoverAdmittedAsync(initializationOperationId, actionId, memberId, member, registration, owner,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseNamespaceBornMemberRecoveryResult> RecoverAdmittedAsync(
        Guid initializationOperationId, Guid actionId, Guid memberId, MirrorPulseNamespaceMemberRegistration member,
        RootRegistration registration, MirrorPulseNamespaceBornRootLabelRecoveryOwner? owner, CancellationToken stop)
    {
        actor.AssertAccess();
        IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction> history = [];
        MirrorPulseNamespaceBornMemberRecoveryAction? retained;
        MirrorPulseNamespaceBornMemberRecoveryResult result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            Progress?.Invoke("NamespaceBornRecoveryContextBegin");
            history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, stop).ConfigureAwait(false);
            retained = history.SingleOrDefault(value => value.ActionId == actionId);
            if (retained is not null && retained.RootLabelRecoveryOwner != owner)
                throw new InvalidOperationException("Born recovery cannot replace its admitted original Label and initializer witness.");
            if (retained is not null && await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(actionId, stop).ConfigureAwait(false) is { } replay)
            {
                if (retained.InitializationOperationId != initializationOperationId) throw new InvalidOperationException("The action belongs to a different initializer.");
                result = new(actionId, replay, null, true); return;
            }
            var child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializationOperationId, memberId, stop).ConfigureAwait(false))[0];
            var currentRoot = router.GetRegistration(registration.InstanceId, registration.UniquenessKey);
            if (child.Member != member || currentRoot.RootId != registration.RootId || currentRoot.DirectoryName != registration.DirectoryName)
                throw new InvalidDataException("The admitted born member or root layout changed before recovery.");
            var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.Location.ParentMemberId!.Value, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("The born parent requires coherent recovery first.");
            if (owner is not null && await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(owner.RootLabelOperationId, stop).ConfigureAwait(false) != owner.Initialization)
                throw new InvalidOperationException("Born cancellation recovery lost its original Label and initializer witness.");
            if (owner is null && parent.Context.Protection.RoleSid != execution.RoleSid.Value || member.OwnerSid != execution.OwnerSid.Value)
                throw new InvalidOperationException("Born recovery requires the live parent role and original current-user owner.");
            string target = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent.Context.Verification.Dacl, member.IsDirectory);
            Progress?.Invoke("NamespaceBornRecoveryContextCompleted");
            CloudItem item = member.IsDirectory ? fileSystem.GetDirectory(child.Location.RelativePath) : fileSystem.GetFile(child.Location.RelativePath);
            var parentItem = parent.Context.Location.RelativePath.Length == 0 ? fileSystem.Root : fileSystem.GetDirectory(parent.Context.Location.RelativePath);
            Progress?.Invoke("NamespaceBornRecoveryGuardsBegin");
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, stop).ConfigureAwait(false);
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, stop).ConfigureAwait(false);
            var parentBefore = await parentGuard.InspectAsync(stop).ConfigureAwait(false);
            RequireObject(parent.Context.Member, parent.Context.Location, parentBefore);
            if (parentBefore.Dacl != parent.Context.Verification.Dacl)
                throw new InvalidDataException("The actual protected parent descriptor changed before born recovery.");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(await parentItem.InspectAsync(stop).ConfigureAwait(false),
                parent.Identity, parent.Context.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
            Progress?.Invoke("NamespaceBornRecoveryGuardsCompleted");
            Progress?.Invoke("NamespaceBornRecoveryActionBegin");
            var action = retained ?? await catalog.PrepareNamespaceBornMemberRecoveryAsync(new(owner is null ? 1 : 2, actionId, initializationOperationId,
                history.Count == 0 ? null : history[^1].ActionId, child, parent, target, DateTimeOffset.UtcNow)
            { RootLabelRecoveryOwner = owner }, stop).ConfigureAwait(false);
            if (action.Parent != parent || action.TargetDacl != target)
                throw new InvalidOperationException("The retained action requires a new immutable successor for this parent role.");
            Progress?.Invoke("NamespaceBornRecoveryActionCompleted");
            if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterActionPrepared, stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryScopeBegin");
            var receipt = await permissions.RunAdmittedBornMemberRecoveryAsync(actionId, (current, permissionToken) =>
                MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(item, router, member.LocalObject, async (lease, scopeToken) =>
                {
                    var before = await lease.InspectAsync(scopeToken).ConfigureAwait(false); RequireObject(member, child.Location, before);
                    var known = history.Select(value => value.TargetDacl).Append(current.Child.BirthProtection!.Verification.Dacl).Append(target);
                    if (!known.Any(dacl => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(dacl, before.Dacl)))
                        throw new InvalidDataException("Born recovery cannot overwrite an unowned descriptor.");
                    if (!member.IsDirectory)
                    {
                        var identity = await lease.InspectLocalIdentityAsync(scopeToken).ConfigureAwait(false);
                        if ((identity.ItemId is null) != (identity.RemoteId is null) ||
                            identity.ItemId is { } actual && (actual != child.Identity.ItemId || identity.RemoteId != child.Identity.RemoteId) ||
                            identity.IsPlaceholder && (identity.PlaceholderIdentity is null || identity.PlaceholderIdentity.ItemId != child.Identity.ItemId ||
                                identity.PlaceholderIdentity.RemoteId != child.Identity.RemoteId))
                            throw new InvalidDataException("Born recovery lost its retained current public identity.");
                        if (!identity.IsPlaceholder || identity.PlaceholderIdentity!.RemoteRevision is null)
                            await lease.PrepareLocalIdentityAsync(new(child.Identity.ItemId!.Value, child.Identity.RemoteId!), scopeToken).ConfigureAwait(false);
                    }
                    if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(target, before.Dacl))
                        await lease.ApplyDaclAsync(target, scopeToken).ConfigureAwait(false);
                    var after = await lease.InspectAsync(scopeToken).ConfigureAwait(false); RequireObject(member, child.Location, after);
                    if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(target, after.Dacl))
                        throw new InvalidDataException("Born recovery did not retain the admitted inherited descriptor.");
                    if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceBornMemberRecoveryCheckpoint.BeforeProtectedCallbackReturns, scopeToken).ConfigureAwait(false);
                }, permissionToken), stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryScopeCompleted");
            if (receipt.Outcome != CloudProtectedLocalOperationOutcome.Completed || !receipt.Drained)
            {
                result = new(actionId, null, receipt, false); return;
            }
            if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterScopeDrained, stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryReadbackBegin");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentGuard.InspectAsync(stop).ConfigureAwait(false));
            var observed = owner is null
                ? await MirrorPulseNamespaceBirthProtectionVerifier.InspectRegisteredAsync(catalog, actionId, memberId, item, parentItem,
                    router, actor, parent, stop).ConfigureAwait(false)
                : await MirrorPulseNamespaceBirthProtectionVerifier.InspectRootLabelRecoveryAsync(catalog, action, item, parentItem,
                    router, actor, stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryReadbackCompleted");
            if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterProtectionReadback, stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryCommitBegin");
            var completion = await catalog.CompleteNamespaceBornMemberRecoveryAsync(actionId, observed, true, DateTimeOffset.UtcNow, stop).ConfigureAwait(false);
            Progress?.Invoke("NamespaceBornRecoveryCommitCompleted");
            if (Checkpoint is not null) await Checkpoint(MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterCompletionRecorded, stop).ConfigureAwait(false);
            result = new(actionId, completion, receipt, false);
        }).ConfigureAwait(false);
        return result;
    }

    private static void RequireObject(MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceMemberLocation location,
        MirrorPulseNamespacePermissionObject actual)
    {
        if (actual.LocalObject != member.LocalObject || actual.OwnerSid != member.OwnerSid || actual.RootId != member.RootId ||
            actual.RelativePath != location.RelativePath || actual.IsDirectory != member.IsDirectory || actual.LinkCount != 1)
            throw new InvalidDataException("Born recovery lost its actual binding, owner, committed path, kind or single-link constraint.");
    }
}
