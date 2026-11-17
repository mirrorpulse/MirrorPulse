using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseTransferPublicationCheckpoint { AfterStart, AfterCreation, AfterObservation, AfterRegistration, BeforeStart }

/// <summary>Publishes the original verified remote target as a separate protected online placeholder.</summary>
/// <remarks>The caller holds the original instance admissions, shared actor and source guards.
/// Startup can settle an observed target from its original protection before role rotation.
/// Source bytes and both durable fences remain retained.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed partial class MirrorPulseNamespaceTransferTargetPublisher(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceActor actor,
    Func<MirrorPulseTransferPublicationCheckpoint, CancellationToken, ValueTask>? checkpoint = null,
    Action<CloudPlaceholderCreationReceipt>? creationReceiptReady = null)
{
    internal async Task<MirrorPulseNamespaceTransferCopy> PublishUnderActorAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, MirrorPulseFileTransferRecovery? activation, CancellationToken token)
    {
        actor.AssertAccess();
        if (copy.TargetPublication is not null) return copy;
        var target = copy.TargetBirth!;
        var (member, protection) = await PublishMemberUnderActorAsync(target.Birth, target.Plan, parent,
            router.GetRegistration(copy.Plan.TargetRoute.InstanceId, copy.Plan.TargetRoute.RootKey),
            copy.TargetVerification!.RemoteId, copy.AcceptedRevision!, token, activation,
            retainedFileRole: copy.TargetRoleSid).ConfigureAwait(false);
        return await RecordFilePublicationAsync(copy, member, protection.ProtectionId).ConfigureAwait(false);
    }

    internal async Task<MirrorPulseNamespaceTransferCopy> SettleObservedFileUnderActorAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceMemberProtectionSnapshot parentSnapshot, CancellationToken token)
    {
        actor.AssertAccess();
        var target = copy.TargetBirth!;
        var (birth, plan) = (target.Birth, target.Plan);
        await RecoverOriginalCreationObservationAsync(birth, plan, token).ConfigureAwait(false);
        if (await catalog.ReadNamespaceMemberAsync(birth.OperationId, token).ConfigureAwait(false) is null)
            await RegisterObservedTransferMemberAsync(birth, plan, parentSnapshot, token).ConfigureAwait(false);
        var current = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(birth.OperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Startup publication requires the original registered target.");
        var member = current.Context.Member;
        if (current.Context.Protection.RoleSid != copy.TargetRoleSid || current.Context.Location.ParentMemberId != parentSnapshot.Context.Member.MemberId ||
            current.Context.Location.RelativePath != birth.RelativePath || current.Context.Location.CurrentPositionOperationId is not null ||
            member.OriginId != birth.OperationId || member.OwnerSid != parentSnapshot.Context.Member.OwnerSid ||
            current.Observation is not { IsInSync: true } observed || observed.LocalObject != member.LocalObject)
            throw new InvalidDataException("Startup publication lost the original target protection or registered location.");

        var parent = fileSystem.GetDirectory(parentSnapshot.Context.Location.RelativePath);
        var item = fileSystem.GetFile(birth.RelativePath);
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent,
            MirrorPulseNamespaceProtectionProofReader.FromSnapshot(parentSnapshot), parentLease, token).ConfigureAwait(false);
        await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
        var child = await childLease.InspectAsync(token).ConfigureAwait(false);
        if (child.LocalObject != member.LocalObject || child.OwnerSid != member.OwnerSid || child.RootId != birth.RootId ||
            child.RelativePath != birth.RelativePath || child.IsDirectory || child.LinkCount != 1 ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.ExpectedDacl, child.Dacl))
            throw new InvalidDataException("Startup publication requires the original owned native file.");
        await RequirePublishedProjectionAsync(birth, plan, item, member, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentLease.InspectAsync(token).ConfigureAwait(false));
        return await RecordFilePublicationAsync(copy, member, current.Context.Protection.ProtectionId).ConfigureAwait(false);
    }

    private Task<MirrorPulseNamespaceTransferCopy> RecordFilePublicationAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceMemberRegistration member, Guid protectionId)
    {
        var target = copy.TargetBirth!;
        return catalog.RecordNamespaceTransferTargetPublicationAsync(new(1, copy.Plan.OperationId, target.Birth.OperationId,
            member.MemberId, target.Plan.ItemId, member.LocalObject, protectionId, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    internal async Task<(MirrorPulseNamespaceMemberRegistration Member, MirrorPulseNamespaceBirthProtection Protection)>
        PublishMemberUnderActorAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
            MirrorPulseNamespaceMemberProtectionSnapshot parentSnapshot, RootRegistration root, string backendId, string revision,
            CancellationToken token, MirrorPulseFileTransferRecovery? activation = null,
            MirrorPulseDirectoryTransferTargetActivation? directoryActivation = null, string? retainedFileRole = null)
    {
        actor.AssertAccess();
        var parent = fileSystem.GetDirectory(parentSnapshot.Context.Location.RelativePath);
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        var proof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(parentSnapshot);
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        if (!MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, backendId, identity) || identity.RemoteRevision != revision ||
            (plan.Content is null) != birth.IsDirectory)
            throw new InvalidDataException("Target publication cannot change its original scoped public identity.");
        CloudPlaceholderSpec specification = OriginalCreationSpecification(birth, plan);
        await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent, proof, guard, token).ConfigureAwait(false);
        var start = await catalog.ReadNamespaceBirthStartAsync(birth.OperationId, token).ConfigureAwait(false);
        var observed = await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false);
        bool create = start is null;
        if (start is null)
        {
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.BeforeStart, token).ConfigureAwait(false);
            if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists) throw new IOException("The native transfer target is occupied.");
            _ = await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, birth.OperationId, item, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(token).ConfigureAwait(false));
            var intent = new MirrorPulseNamespaceBirthStart(2, birth.OperationId, DateTimeOffset.UtcNow);
            var prepared = directoryActivation is not null
                ? await catalog.RecordReactivatedDirectoryTransferBirthStartAsync(intent, directoryActivation, token).ConfigureAwait(false)
                : activation is not null
                    ? await catalog.RecordReactivatedFileTransferBirthStartAsync(intent, activation, token).ConfigureAwait(false)
                    : await catalog.RecordNamespaceBirthStartAsync(intent, token).ConfigureAwait(false);
            if (!prepared.NewlyRecorded) throw new InvalidOperationException("A retained birth start cannot authorize another native creation.");
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterStart, token).ConfigureAwait(false);
        }
        else if (start.Version == 2 && observed is null)
        {
            string? retainedRole = birth.TransferEntry is { Version: 2 }
                ? await catalog.ReadDirectoryTransferCreationRoleAsync(birth.OperationId, token).ConfigureAwait(false) : retainedFileRole;
            observed = await RecoverOriginalCreationObservationAsync(birth, plan, token, allowUnprepared: retainedRole is not null).ConfigureAwait(false);
            if (observed is null)
            {
                // The original SDK store has no preparation for this member. Only the role
                // admitted by the retained transfer may invoke the immutable original request.
                using var caller = WindowsIdentity.GetCurrent();
                if (retainedRole is null || proof.RoleSid != retainedRole || caller.User?.Value != proof.OwnerSid ||
                    !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(retainedRole)))
                    throw new InvalidOperationException("The original unprepared SDK creation still requires its admitted role.");
                if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new IOException("The native transfer target is occupied.");
                await MirrorPulseNamespaceBirthIdentityProjection.RequireUnboundStartedProjectionAsync(catalog, state.OpenStore, birth.OperationId, item, token)
                    .ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(token).ConfigureAwait(false));
                create = true;
            }
        }
        if (create)
        {
            var results = await parent.CreateRecoverablePlaceholdersAsync([new(birth.OperationId, specification)],
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (results.Count != 1 || results[0].OperationId != birth.OperationId ||
                results[0].Status != CloudPlaceholderCreationRecoveryStatus.Created || results[0].Receipt is null)
                throw new InvalidOperationException($"The original transfer target creation has no committed public receipt: {(results.Count == 1 ? results[0].Status.ToString() : "InvalidBatch")}.",
                    results.Count == 1 ? results[0].Error : null);
            creationReceiptReady?.Invoke(results[0].Receipt!);
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterCreation, CancellationToken.None).ConfigureAwait(false);
            observed = await MirrorPulseNamespaceBirthObserver.RecordRecoveredCreationAsync(catalog, birth.OperationId, item, router,
                results[0].Receipt!, CancellationToken.None).ConfigureAwait(false);
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterObservation, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (observed is null)
                throw new InvalidOperationException("A started target birth without its first native binding requires explicit outcome recovery.");
            _ = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, token).ConfigureAwait(false);
            await using var childGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(item, router, token).ConfigureAwait(false);
            var childBefore = await childGuard.InspectAsync(token).ConfigureAwait(false);
            if (childBefore.LocalObject != observed.LocalObject || childBefore.OwnerSid != observed.OwnerSid || childBefore.LinkCount != 1 ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                    MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, birth.IsDirectory), childBefore.Dacl))
                throw new InvalidDataException("Target projection recovery requires its original object and current owned inherited protection.");
            // A prepared logical row is not a completed native projection. Replay the SDK's identity-match
            // transaction only after binding the known original object. OnlineOnly performs no hydration,
            // dehydration, content write, mark or pin work, including after a later local edit.
            var repaired = await parent.CreatePlaceholdersAsync([specification], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            repaired.ThrowIfAnyFailed();
            if (repaired.Entries.Count != 1 || !repaired.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.ExistingPlaceholderMatched) ||
                repaired.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.PlaceholderCreated))
                throw new InvalidDataException("SDK projection recovery must retain the exact previously observed native placeholder.");
            var childAfter = await childGuard.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            if (childAfter.LocalObject != childBefore.LocalObject || childAfter.OwnerSid != childBefore.OwnerSid ||
                childAfter.Dacl != childBefore.Dacl || childAfter.RootId != childBefore.RootId ||
                childAfter.RelativePath != childBefore.RelativePath || childAfter.IsDirectory != birth.IsDirectory || childAfter.LinkCount != 1)
                throw new InvalidDataException("The original transfer target changed during SDK projection recovery.");
            _ = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, CancellationToken.None).ConfigureAwait(false);
        }
        CancellationToken durable = CancellationToken.None;
        if (!await HasProjectionAsync(birth, plan, item, durable).ConfigureAwait(false))
            throw new InvalidDataException("The original target has no complete official SDK projection.");
        var latest = await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId, durable).ConfigureAwait(false);
        var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, latest?.ProtectionId ?? Guid.NewGuid(),
            birth.OperationId, item, parent, router, durable).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(durable).ConfigureAwait(false));
        var position = parentSnapshot.Context.Location.CurrentPositionOperationId;
        var member = await catalog.ReadNamespaceMemberAsync(birth.OperationId, durable).ConfigureAwait(false)
            ?? new MirrorPulseNamespaceMemberRegistration(position is null ? 1 : 2, birth.OperationId,
                parentSnapshot.Context.Member.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, birth.OperationId,
                protection.ProtectionId, birth.RootId, birth.RelativePath, observed!.LocalObject, birth.IsDirectory, observed.OwnerSid, DateTimeOffset.UtcNow)
            { FirstParentPositionOperationId = position };
        member = await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, durable).ConfigureAwait(false);
        await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterRegistration, durable).ConfigureAwait(false);
        await RequirePublishedProjectionAsync(birth, plan, item, member, durable).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(durable).ConfigureAwait(false));
        return (member, protection);
    }

    private async Task RequirePublishedProjectionAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
        CloudItem item, MirrorPulseNamespaceMemberRegistration member, CancellationToken token)
    {
        if (!await HasProjectionAsync(birth, plan, item, token).ConfigureAwait(false))
            throw new InvalidDataException("The native target projection changed before its first publication receipt.");
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (!native.Exists || native.IsTombstone || !native.IsPlaceholder || native.Kind != item.Kind ||
            native.ItemId != plan.ItemId || native.RemoteId != plan.RemoteId || native.RemoteRevision != plan.RemoteRevision ||
            plan.Content is { } content && native.Length != content.Length || native.SynchronizationState != CloudSynchronizationState.InSync ||
            native.LocalBinding is not { } binding || member.LocalObject != new MirrorPulseLocalFileBinding(
                binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            !native.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision).Encode()))
            throw new InvalidDataException("The original native target changed before its first accepted placeholder publication.");
    }

    internal async Task<bool> SettleObservedDirectoryTransferMemberAsync(MirrorPulseDirectoryTransferTargetBirth target, CancellationToken token)
    {
        actor.AssertAccess();
        var birth = target.Birth;
        if (await catalog.ReadNamespaceMemberAsync(birth.OperationId, token).ConfigureAwait(false) is not null)
            return false;
        var parentMember = await catalog.ReadNamespaceMemberByBindingAsync(birth.ParentLocalObject, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending target lost its registered direct parent.");
        var parentSnapshot = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parentMember.MemberId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending target lost its verified parent.");
        await RecoverOriginalCreationObservationAsync(birth, target.Plan, token).ConfigureAwait(false);
        await RegisterObservedTransferMemberAsync(birth, target.Plan, parentSnapshot, token).ConfigureAwait(false);
        return true;
    }

    private async Task RegisterObservedTransferMemberAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
        MirrorPulseNamespaceMemberProtectionSnapshot parentSnapshot, CancellationToken token)
    {
        var observed = await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending target lost its original native observation.");
        var parent = fileSystem.GetDirectory(parentSnapshot.Context.Location.RelativePath);
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(
            birth, parent, MirrorPulseNamespaceProtectionProofReader.FromSnapshot(parentSnapshot), parentLease, token).ConfigureAwait(false);
        await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (!native.Exists || !native.IsPlaceholder || native.IsTombstone || native.Kind != item.Kind ||
            native.LocalBinding is not { } binding ||
            observed.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            native.SynchronizationState != CloudSynchronizationState.InSync ||
            (plan.Content is { } content && native.Length != content.Length) ||
            !native.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision).Encode()) ||
            !await HasProjectionAsync(birth, plan, item, token).ConfigureAwait(false))
            throw new InvalidDataException("Startup registration requires the original observed placeholder and official projection.");
        var latest = await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId, token).ConfigureAwait(false);
        var protection = await MirrorPulseNamespaceBirthProtectionVerifier.SettleObservedTransferAsync(
            catalog, latest?.ProtectionId ?? Guid.NewGuid(), birth, plan, item, parent, router, token).ConfigureAwait(false);
        var position = parentSnapshot.Context.Location.CurrentPositionOperationId;
        var member = new MirrorPulseNamespaceMemberRegistration(position is null ? 1 : 2, birth.OperationId,
            parentSnapshot.Context.Member.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, birth.OperationId, protection.ProtectionId,
            birth.RootId, birth.RelativePath, observed.LocalObject, birth.IsDirectory, observed.OwnerSid, DateTimeOffset.UtcNow)
        { FirstParentPositionOperationId = position };
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before,
            await parentLease.InspectAsync(token).ConfigureAwait(false));
        _ = await MirrorPulseNamespaceMemberRegistrar.RegisterObservedTransferAsync(catalog, birth, plan, member, item, parent,
            router, actor, token).ConfigureAwait(false);
    }

    private async Task<bool> HasProjectionAsync(MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
        CloudItem item, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var id = await transaction.Items.GetByItemIdAsync(plan.ItemId, token).ConfigureAwait(false);
        var path = await transaction.Items.GetByRelativePathAsync(item.RelativePath, token).ConfigureAwait(false);
        var remote = await transaction.Items.GetByRemoteIdAsync(plan.RemoteId, token).ConfigureAwait(false);
        bool matches = Matches(id) && Matches(path) && Matches(remote) && id!.UpdatedAt == path!.UpdatedAt && id.UpdatedAt == remote!.UpdatedAt;
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return matches;

        bool Matches(CloudItemState? row) => row is not null && !row.IsTombstone && row.Kind == item.Kind &&
            row.ItemId == plan.ItemId && row.RemoteId == plan.RemoteId && row.RemoteRevision == plan.RemoteRevision &&
            row.RelativePath.Replace('\\', '/') == birth.RelativePath;
    }

    private static void RequireCreated(CloudPlaceholderBatchResult result, CloudPlaceholderSpec specification)
    {
        if (result.Entries.Count != 1 || result.Entries[0].Specification != specification ||
            !result.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.PlaceholderCreated) ||
            result.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.ExistingPlaceholderMatched))
            throw new InvalidDataException("The first target birth requires its original native creation, not an identity-matched occupant.");
    }

    private ValueTask CheckpointAsync(MirrorPulseTransferPublicationCheckpoint checkpointValue, CancellationToken token) =>
        checkpoint?.Invoke(checkpointValue, token) ?? ValueTask.CompletedTask;
}
