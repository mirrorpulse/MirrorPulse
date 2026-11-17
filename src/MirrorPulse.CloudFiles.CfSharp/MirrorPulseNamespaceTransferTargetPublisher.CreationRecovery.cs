using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed partial class MirrorPulseNamespaceTransferTargetPublisher
{
    internal async Task RequireUnpreparedFileCreationUnderActorAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceBirthStart start, bool rootsAudited, CancellationToken token)
    {
        actor.AssertAccess();
        if (copy.TargetBirth is not { } target || copy.TargetPublication is not null || target.Birth.IsDirectory)
            throw new InvalidOperationException("Role recovery requires the original started target before its first observation.");
        var original = copy.Plan.TargetParent;
        Guid memberId = original.Context.Member.MemberId;
        if (await catalog.ReadNamespaceMemberAsync(memberId, token).ConfigureAwait(false) != original.Context.Member ||
            await catalog.ReadNamespaceMemberLocationAsync(memberId, token).ConfigureAwait(false) != original.Context.Location ||
            await catalog.ReadNamespaceMemberIdentityAsync(memberId, token).ConfigureAwait(false) != original.Identity)
            throw new InvalidOperationException("Role recovery requires the original target parent and public identity.");
        await RequireUnpreparedCreationUnderActorAsync(target.Birth, target.Plan, start, original.Context.Member, rootsAudited, token)
            .ConfigureAwait(false);
    }

    internal async Task RequireUnpreparedDirectoryTransferMemberUnderActorAsync(MirrorPulseDirectoryTransferTargetBirth target,
        MirrorPulseNamespaceBirthStart start, bool rootsAudited, CancellationToken token)
    {
        actor.AssertAccess();
        var parent = await catalog.ReadNamespaceMemberByBindingAsync(target.Birth.ParentLocalObject, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Directory recovery requires the original registered target parent.");
        await RequireUnpreparedCreationUnderActorAsync(target.Birth, target.Plan, start, parent, rootsAudited, token).ConfigureAwait(false);
    }

    private async Task RequireUnpreparedCreationUnderActorAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
        MirrorPulseNamespaceBirthStart start, MirrorPulseNamespaceMemberRegistration originalParent, bool rootsAudited, CancellationToken token)
    {
        if (start.Version != 2 || start.OperationId != birth.OperationId ||
            await catalog.ReadNamespaceBirthStartAsync(start.OperationId, token).ConfigureAwait(false) != start ||
            await catalog.ReadNamespaceBirthObservationAsync(start.OperationId, token).ConfigureAwait(false) is not null ||
            await catalog.ReadNamespaceMemberAsync(start.OperationId, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Role recovery requires the original started target before its first observation.");
        string parentPath = birth.RelativePath[..birth.RelativePath.LastIndexOf('/')];
        var location = await catalog.ReadNamespaceMemberLocationAsync(originalParent.MemberId, token).ConfigureAwait(false);
        if (location.RelativePath != parentPath)
            throw new InvalidOperationException("Role recovery requires the original target parent at its retained path.");
        var parent = fileSystem.GetDirectory(parentPath);
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        // Startup may already have moved the shared anchor to its new role. Check the exact
        // direct parent before auditing and require coherent current protection afterwards.
        var before = await lease.InspectAsync(token).ConfigureAwait(false);
        if (before.LocalObject != birth.ParentLocalObject || before.OwnerSid != originalParent.OwnerSid ||
            before.RelativePath != parentPath || before.RootId != birth.RootId ||
            !before.IsDirectory || before.LinkCount != 1)
            throw new InvalidOperationException("Role recovery requires the original native target parent.");
        if (rootsAudited)
        {
            var snapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(originalParent.MemberId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Role recovery requires audited native parent protection.");
            before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent,
                MirrorPulseNamespaceProtectionProofReader.FromSnapshot(snapshot), lease, token).ConfigureAwait(false);
        }
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        var result = await parent.RecoverPlaceholderCreationAsync(new(start.OperationId, OriginalCreationSpecification(birth, plan)),
            token).ConfigureAwait(false);
        if (!IsUnpreparedCreation(result, start.OperationId) || (await item.InspectAsync(token).ConfigureAwait(false)).Exists)
            throw new InvalidOperationException($"The original transfer target cannot enter role recovery before SDK preparation: {result.Status}.", result.Error);
        await MirrorPulseNamespaceBirthIdentityProjection.RequireUnboundStartedProjectionAsync(catalog, state.OpenStore,
            start.OperationId, item, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await lease.InspectAsync(token).ConfigureAwait(false));
    }

    private static bool IsUnpreparedCreation(CloudPlaceholderCreationRecoveryResult result, Guid operationId) =>
        result.OperationId == operationId && result.Status == CloudPlaceholderCreationRecoveryStatus.NotFound &&
        result.Receipt is null && result.Error is null;

    private static CloudPlaceholderSpec OriginalCreationSpecification(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan)
    {
        string name = birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..];
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        return birth.IsDirectory
            ? CloudDirectoryPlaceholderSpec.CreateBuilder(name, identity).WithInSyncState(true)
                .WithCollisionBehavior(CloudPlaceholderCollisionBehavior.Fail).WithPopulationState(CloudDirectoryPopulationState.Complete).Build()
            : CloudFilePlaceholderSpec.CreateBuilder(name, identity, plan.Content!.Length).WithInSyncState(true)
                .WithCollisionBehavior(CloudPlaceholderCollisionBehavior.Fail).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build();
    }

    private async Task<MirrorPulseNamespaceBirthObservation?> RecoverOriginalCreationObservationAsync(
        MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan, CancellationToken token,
        bool allowUnprepared = false)
    {
        actor.AssertAccess();
        if (await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false) is { } observed) return observed;
        var start = await catalog.ReadNamespaceBirthStartAsync(birth.OperationId, token).ConfigureAwait(false);
        if (start?.Version != 2) return null;
        string parentPath = birth.RelativePath[..birth.RelativePath.LastIndexOf('/')];
        var parent = fileSystem.GetDirectory(parentPath);
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(token).ConfigureAwait(false);
        if (before.LocalObject != birth.ParentLocalObject || before.RelativePath != parentPath || !before.IsDirectory || before.RootId != birth.RootId)
            throw new InvalidDataException("Creation recovery requires the original native parent.");
        var recovered = await parent.RecoverPlaceholderCreationAsync(new(birth.OperationId, OriginalCreationSpecification(birth, plan)), token)
            .ConfigureAwait(false);
        if (allowUnprepared && IsUnpreparedCreation(recovered, birth.OperationId))
        {
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentLease.InspectAsync(token).ConfigureAwait(false));
            return null;
        }
        if (recovered.OperationId != birth.OperationId || recovered.Status != CloudPlaceholderCreationRecoveryStatus.Recovered || recovered.Receipt is null)
            throw new InvalidOperationException($"The original transfer target creation remains pending: {recovered.Status}.", recovered.Error);
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        var result = await MirrorPulseNamespaceBirthObserver.RecordRecoveredCreationAsync(catalog, birth.OperationId, item, router,
            recovered.Receipt, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentLease.InspectAsync(token).ConfigureAwait(false));
        return result;
    }
}
