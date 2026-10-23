using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseTransferPublicationCheckpoint { AfterStart, AfterCreation, AfterObservation, AfterRegistration }

/// <summary>Publishes the original verified remote target as a separate protected online placeholder.</summary>
/// <remarks>The caller holds both root/instance admissions, the namespace role, shared actor and source guards.
/// No remote batch or upload acceptance is synthesized. Source bytes and both durable fences remain retained.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseNamespaceTransferTargetPublisher(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceActor actor,
    Func<MirrorPulseTransferPublicationCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    internal async Task<MirrorPulseNamespaceTransferCopy> PublishUnderActorAsync(MirrorPulseNamespaceTransferCopy copy, CancellationToken token)
    {
        actor.AssertAccess();
        copy = await catalog.ValidateNamespaceTransferTargetPublicationAsync(copy.Plan.OperationId, token).ConfigureAwait(false);
        if (copy.TargetPublication is not null) return copy;
        var target = copy.TargetBirth!; var birth = target.Birth; var plan = target.Plan;
        var parent = fileSystem.GetDirectory(copy.Plan.TargetParent.Context.Location.RelativePath);
        var item = fileSystem.GetFile(birth.RelativePath);
        var proof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(copy.Plan.TargetParent);
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        if (!MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(router.GetRegistration(copy.Plan.TargetRoute.InstanceId,
                copy.Plan.TargetRoute.RootKey), copy.TargetVerification!.RemoteId, identity) || identity.RemoteRevision != copy.AcceptedRevision)
            throw new InvalidDataException("Target publication cannot change its original scoped public identity.");
        var specification = CloudFilePlaceholderSpec.CreateBuilder(birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..], identity, copy.Plan.Length).WithInSyncState(true)
            .WithCollisionBehavior(CloudPlaceholderCollisionBehavior.Fail).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build();
        await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
        var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(birth, parent, proof, guard, token).ConfigureAwait(false);
        var start = await catalog.ReadNamespaceBirthStartAsync(birth.OperationId, token).ConfigureAwait(false);
        var observed = await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId, token).ConfigureAwait(false);
        if (start is null)
        {
            if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists) throw new IOException("The native transfer target is occupied.");
            _ = await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, birth.OperationId, item, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(token).ConfigureAwait(false));
            await catalog.RecordNamespaceBirthStartAsync(new(1, birth.OperationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterStart, token).ConfigureAwait(false);
            CloudPlaceholderBatchResult created;
            try { created = await parent.CreatePlaceholdersAsync([specification], cancellationToken: CancellationToken.None).ConfigureAwait(false); }
            catch (CloudPlaceholderPersistenceException failure)
            {
                // A known native result can retain its first complete binding even when the SDK store fails.
                // It is never a publication receipt; recovery must still repair the actual SDK projection.
                RequireCreated(failure.AppliedResult, specification);
                await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            created.ThrowIfAnyFailed(); RequireCreated(created, specification);
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterCreation, CancellationToken.None).ConfigureAwait(false);
            observed = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, CancellationToken.None).ConfigureAwait(false);
            await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterObservation, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (observed is null)
                throw new InvalidOperationException("A started target birth without its first native binding requires explicit outcome recovery; creation cannot repeat.");
            _ = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, token).ConfigureAwait(false);
            await using var childGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(item, router, token).ConfigureAwait(false);
            var childBefore = await childGuard.InspectAsync(token).ConfigureAwait(false);
            if (childBefore.LocalObject != observed.LocalObject || childBefore.OwnerSid != observed.OwnerSid || childBefore.LinkCount != 1 ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                    MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, false), childBefore.Dacl))
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
                childAfter.RelativePath != childBefore.RelativePath || childAfter.IsDirectory || childAfter.LinkCount != 1)
                throw new InvalidDataException("The original transfer target changed during SDK projection recovery.");
            _ = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, birth.OperationId, item, router, CancellationToken.None).ConfigureAwait(false);
        }
        CancellationToken durable = CancellationToken.None;
        if (!await HasProjectionAsync(target, item, durable).ConfigureAwait(false))
            throw new InvalidDataException("The original target has no complete official SDK projection.");
        var latest = await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId, durable).ConfigureAwait(false);
        var protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, latest?.ProtectionId ?? Guid.NewGuid(),
            birth.OperationId, item, parent, router, durable).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(durable).ConfigureAwait(false));
        var position = copy.Plan.TargetParent.Context.Location.CurrentPositionOperationId;
        var member = await catalog.ReadNamespaceMemberAsync(birth.OperationId, durable).ConfigureAwait(false)
            ?? new MirrorPulseNamespaceMemberRegistration(position is null ? 1 : 2, birth.OperationId,
                copy.Plan.TargetParent.Context.Member.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, birth.OperationId,
                protection.ProtectionId, birth.RootId, birth.RelativePath, observed!.LocalObject, false, observed.OwnerSid, DateTimeOffset.UtcNow)
            { FirstParentPositionOperationId = position };
        member = await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, durable).ConfigureAwait(false);
        await CheckpointAsync(MirrorPulseTransferPublicationCheckpoint.AfterRegistration, durable).ConfigureAwait(false);
        if (!await HasProjectionAsync(target, item, durable).ConfigureAwait(false))
            throw new InvalidDataException("The native target projection changed before its first publication receipt.");
        var native = await item.InspectAsync(durable).ConfigureAwait(false);
        if (!native.Exists || native.IsTombstone || !native.IsPlaceholder || native.Kind != CloudItemKind.File ||
            native.ItemId != plan.ItemId || native.RemoteId != plan.RemoteId || native.RemoteRevision != plan.RemoteRevision ||
            native.Length != copy.Plan.Length || native.SynchronizationState != CloudSynchronizationState.InSync ||
            native.LocalBinding is not { } binding || member.LocalObject != new MirrorPulseLocalFileBinding(
                binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
            throw new InvalidDataException("The original native target changed before its first accepted placeholder publication.");
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await guard.InspectAsync(durable).ConfigureAwait(false));
        return await catalog.RecordNamespaceTransferTargetPublicationAsync(new(1, copy.Plan.OperationId, birth.OperationId,
            member.MemberId, plan.ItemId, member.LocalObject, protection.ProtectionId, DateTimeOffset.UtcNow), durable).ConfigureAwait(false);
    }

    private async Task<bool> HasProjectionAsync(MirrorPulseNamespaceTransferTargetBirth target, CloudFile item, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var id = await transaction.Items.GetByItemIdAsync(target.Plan.ItemId, token).ConfigureAwait(false);
        var path = await transaction.Items.GetByRelativePathAsync(item.RelativePath, token).ConfigureAwait(false);
        var remote = await transaction.Items.GetByRemoteIdAsync(target.Plan.RemoteId, token).ConfigureAwait(false);
        bool matches = Matches(id) && Matches(path) && Matches(remote) && id!.UpdatedAt == path!.UpdatedAt && id.UpdatedAt == remote!.UpdatedAt;
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return matches;

        bool Matches(CloudItemState? row) => row is not null && !row.IsTombstone && row.Kind == CloudItemKind.File &&
            row.ItemId == target.Plan.ItemId && row.RemoteId == target.Plan.RemoteId && row.RemoteRevision == target.Plan.RemoteRevision &&
            row.RelativePath.Replace('\\', '/') == target.Birth.RelativePath;
    }

    private static void RequireCreated(CloudPlaceholderBatchResult result, CloudFilePlaceholderSpec specification)
    {
        if (result.Entries.Count != 1 || result.Entries[0].Specification != specification ||
            !result.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.PlaceholderCreated) ||
            result.Entries[0].Progress.HasFlag(CloudPlaceholderCreationProgress.ExistingPlaceholderMatched))
            throw new InvalidDataException("The first target birth requires its original native creation, not an identity-matched occupant.");
    }

    private ValueTask CheckpointAsync(MirrorPulseTransferPublicationCheckpoint checkpointValue, CancellationToken token) =>
        checkpoint?.Invoke(checkpointValue, token) ?? ValueTask.CompletedTask;
}
