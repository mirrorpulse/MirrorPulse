using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseDirectoryConfirmationCheckpoint { AfterAcceptanceSaved, AfterNativeApplied, BeforeConfirmationRecorded }

/// <summary>Confirms a registered directory through its original native object and the shared Host namespace role.</summary>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseNamespaceDirectoryConfirmation(MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseCfSharpStateSession state, MirrorPulseNamespaceActor? actor, MirrorPulseNamespaceExecutionSession? execution)
{
    internal Func<MirrorPulseDirectoryConfirmationCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal async ValueTask ConfirmAsync(CloudFileSystem fileSystem, MirrorPulseMutationRecord accepted,
        MirrorPulseWorkerDirectoryEntry remote, string? revision, Guid? originalItemId, CancellationToken token)
    {
        var intent = accepted.Intent;
        if (!intent.IsDirectory || intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.Move) ||
            accepted.State != MirrorPulseMutationState.RemoteAccepted || accepted.AcceptedRevision != revision ||
            string.IsNullOrEmpty(revision) || remote.IsDeleted || remote.ItemKind != "Directory" || remote.RemoteRevision != revision)
            throw new MirrorPulseMutationAmbiguousException("Directory confirmation requires the actual unchanged typed remote acceptance.");
        string path = router.ResolveUploadPath(intent.InstanceId, intent.RootKey, intent.RelativePath);
        var directory = fileSystem.GetDirectory(Path.GetRelativePath(fileSystem.Root.FullPath, path).Replace('\\', '/'));
        var snapshot = await directory.InspectAsync(token).ConfigureAwait(false);
        var binding = snapshot.LocalBinding is { } local
            ? new MirrorPulseLocalFileBinding(local.VolumeSerialNumber, local.SyncRootFileId, local.LocalFileId) : null;
        if (binding is null || !snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.Directory)
            throw new MirrorPulseMutationAmbiguousException("The accepted directory lost its actual native object.");
        if (actor is null)
        {
            if (await catalog.ReadNamespaceMemberByBindingAsync(binding, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Registered directory confirmation requires the shared namespace actor and role.");
            await MirrorPulseDirectoryConfirmation.ConfirmAsync(fileSystem, router, intent, remote, revision, token).ConfigureAwait(false);
            return;
        }
        await actor.RunAsync(async admitted =>
        {
            actor.AssertAccess();
            var member = await catalog.ReadNamespaceMemberByBindingAsync(binding, admitted).ConfigureAwait(false);
            if (member is null)
            {
                await MirrorPulseDirectoryConfirmation.ConfirmAsync(fileSystem, router, intent, remote, revision, admitted).ConfigureAwait(false);
                return true;
            }
            if (execution is null) throw new InvalidOperationException("Registered directory confirmation requires the shared namespace execution role.");
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Protected directory confirmation requires Windows 11 version 24H2 or later.");
            var root = router.GetRegistration(intent.InstanceId, intent.RootKey);
            var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, admitted).ConfigureAwait(false);
            if (!member.IsDirectory || member.RootId != root.RootId || root.State != RootRegistrationState.Active ||
                location.RelativePath != directory.RelativePath.Replace('\\', '/') ||
                (await catalog.ReadNamespaceRecoveryRootsAsync(admitted).ConfigureAwait(false)).Contains(root.RootId))
                throw new InvalidOperationException("Directory confirmation requires its committed position and unfenced active root.");
            var proof = await catalog.ReadDirectoryAcceptanceAsync(intent.OperationId, admitted).ConfigureAwait(false);
            if (proof is null)
            {
                var previous = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, admitted).ConfigureAwait(false);
                if (originalItemId is null)
                {
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(admitted).ConfigureAwait(false);
                    var original = await transaction.Operations.GetAsync(intent.OperationId, admitted).ConfigureAwait(false);
                    if (original?.Kind != (intent.Kind == MirrorPulseWorkerChangeKind.Create ? CloudStateOperationKind.Create : CloudStateOperationKind.Move))
                        throw new InvalidDataException("Directory confirmation lost its original official journal operation.");
                    originalItemId = original.ItemId;
                    await transaction.RollbackAsync(admitted).ConfigureAwait(false);
                }
                if (previous.ItemId is null || previous.RemoteId is null || previous.ItemId != originalItemId)
                    throw new InvalidDataException("Directory acceptance cannot adopt a different member from the current path.");
                var identity = await router.CreateCurrentFileIdentityAsync(intent.InstanceId, intent.RootKey, remote.RemoteId, revision, admitted).ConfigureAwait(false);
                proof = await catalog.PrepareDirectoryAcceptanceAsync(new(1, intent.OperationId, member.MemberId, root.RootId,
                    member.LocalObject, previous.AcceptanceOperationId, previous.ItemId.Value, previous.RemoteId,
                    identity.ItemId, identity.RemoteId, revision, location.RelativePath, location.CurrentPositionOperationId,
                    MirrorPulseProductCatalog.ComputeDirectoryAcceptanceIntentSha256(intent)), admitted).ConfigureAwait(false);
            }
            if (proof.MemberId != member.MemberId || proof.LocalObject != binding || proof.RelativePath != location.RelativePath ||
                proof.PositionOperationId != location.CurrentPositionOperationId || proof.AcceptedRevision != revision ||
                proof.IntentSha256 != MirrorPulseProductCatalog.ComputeDirectoryAcceptanceIntentSha256(intent))
                throw new InvalidDataException("Directory recovery cannot adopt another accepted mutation, object or position.");
            if (Checkpoint is not null) await Checkpoint(MirrorPulseDirectoryConfirmationCheckpoint.AfterAcceptanceSaved, admitted).ConfigureAwait(false);
            await ConfirmProtectedAsync(directory, member, location, proof, execution, admitted).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task ConfirmProtectedAsync(CloudDirectory directory, MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberLocation location, MirrorPulseDirectoryAcceptance proof,
        MirrorPulseNamespaceExecutionSession role, CancellationToken token)
    {
        var current = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
        if (current.AcceptanceOperationId == proof.OperationId ? current.ItemId != proof.AcceptedItemId || current.RemoteId != proof.RemoteId :
            current.AcceptanceOperationId != proof.PreviousAcceptanceOperationId || current.ItemId != proof.PreviousItemId || current.RemoteId != proof.PreviousRemoteId)
            throw new InvalidDataException("Directory confirmation cannot rewind a later identity acceptance.");
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        var parentMember = await catalog.ReadNamespaceMemberAsync(location.ParentMemberId
            ?? throw new InvalidDataException("A confirmed directory requires its committed parent."), token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory's committed parent is missing.");
        var parentLocation = await catalog.ReadNamespaceMemberLocationAsync(parentMember.MemberId, token).ConfigureAwait(false);
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        var parent = directory.Parent ?? throw new InvalidDataException("A confirmed directory requires its actual direct parent.");
        if (parent.RelativePath.Replace('\\', '/') != parentLocation.RelativePath || parentMember.RootId is { } root && root != member.RootId ||
            childProof.RoleSid != role.RoleSid.Value || parentProof.RoleSid != role.RoleSid.Value ||
            member.OwnerSid != role.OwnerSid.Value || parentMember.OwnerSid != role.OwnerSid.Value)
            throw new InvalidDataException("Directory confirmation requires its current owner, role and committed direct parent.");
        await role.RunNamespaceOperationAsync(async () =>
        {
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(directory, router, token).ConfigureAwait(false);
            var parentBefore = await parentLease.InspectAsync(token).ConfigureAwait(false);
            RequireProtected(parentBefore, await parent.InspectAsync(token).ConfigureAwait(false), parentMember, parentProof, parentLocation.RelativePath);
            var before = await directory.InspectAsync(token).ConfigureAwait(false);
            RequireProtected(await childLease.InspectAsync(token).ConfigureAwait(false), before, member, childProof, location.RelativePath);
            if (!before.IsPlaceholder) throw new InvalidDataException("Registered directory confirmation cannot convert another ordinary object.");
            var actual = CloudPlaceholderIdentity.Decode(before.PlaceholderIdentity.Span);
            var identity = new CloudPlaceholderIdentity(proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision);
            bool already = before.PlaceholderIdentity.Span.SequenceEqual(identity.Encode());
            if (!already && (actual.ItemId != proof.PreviousItemId || actual.RemoteId != proof.PreviousRemoteId))
                throw new InvalidDataException("Directory confirmation requires its retained predecessor or exact accepted identity.");
            if (!already || before.SynchronizationState != CloudSynchronizationState.InSync)
                await directory.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentity(identity).WithInSyncState(true).Build(), token).ConfigureAwait(false);
            CancellationToken durable = CancellationToken.None;
            if (Checkpoint is not null) await Checkpoint(MirrorPulseDirectoryConfirmationCheckpoint.AfterNativeApplied, durable).ConfigureAwait(false);
            var after = await directory.InspectAsync(durable).ConfigureAwait(false);
            RequireProtected(await childLease.InspectAsync(durable).ConfigureAwait(false), after, member, childProof, location.RelativePath);
            RequireProtected(await parentLease.InspectAsync(durable).ConfigureAwait(false), await parent.InspectAsync(durable).ConfigureAwait(false), parentMember, parentProof, parentLocation.RelativePath);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentLease.InspectAsync(durable).ConfigureAwait(false));
            if (!after.IsPlaceholder || after.SynchronizationState != CloudSynchronizationState.InSync || !after.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()) ||
                await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, durable).ConfigureAwait(false) != childProof ||
                await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, durable).ConfigureAwait(false) != parentProof)
                throw new InvalidDataException("Directory confirmation lost its accepted native identity or protection.");
            await using (var transaction = await state.OpenStore.BeginTransactionAsync(durable).ConfigureAwait(false))
            {
                var row = await transaction.Items.GetByItemIdAsync(proof.AcceptedItemId, durable).ConfigureAwait(false);
                if (row is null || row.IsTombstone || row.Kind != CloudItemKind.Directory || row.RemoteId != proof.RemoteId ||
                    row.RemoteRevision != proof.AcceptedRevision ||
                    row.RelativePath.Replace('\\', '/') != location.RelativePath)
                    throw new InvalidDataException("The public directory operation did not commit its exact official projection.");
                // Public identity patches intentionally leave the SDK's legacy 64-bit ID null.
                // The retained full native binding was checked independently on both sides of the update.
                await transaction.RollbackAsync(durable).ConfigureAwait(false);
            }
            actor!.AssertAccess();
            if (Checkpoint is not null) await Checkpoint(MirrorPulseDirectoryConfirmationCheckpoint.BeforeConfirmationRecorded, durable).ConfigureAwait(false);
            if (await catalog.ReadDirectoryConfirmationAsync(proof.OperationId, durable).ConfigureAwait(false) is null)
                await catalog.RecordDirectoryConfirmationAsync(new(proof.OperationId, member.LocalObject,
                    Convert.ToBase64String(after.PlaceholderIdentity.Span), identity.ItemId, identity.RemoteId, identity.RemoteRevision!,
                    true, true, DateTimeOffset.UtcNow), durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private static void RequireProtected(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceProtectionProof proof, string path)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.LocalObject != member.LocalObject || facts.LocalObject != proof.Verification.LocalObject ||
            facts.OwnerSid != member.OwnerSid || facts.OwnerSid != proof.OwnerSid || facts.OwnerSid != proof.Verification.OwnerSid ||
            facts.RootId != member.RootId || facts.RootId != proof.RootId || facts.RelativePath != path ||
            facts.IsDirectory != member.IsDirectory || facts.IsDirectory != proof.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory || facts.LinkCount != 1 ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("Directory confirmation requires the same protected object at its committed location.");
    }
}
