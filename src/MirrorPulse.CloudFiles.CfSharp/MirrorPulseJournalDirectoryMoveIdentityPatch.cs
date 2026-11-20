using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Applies a retained target address to the same protected object without confirming its content.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseJournalDirectoryMoveIdentityPatch(CloudFileSystem fileSystem,
    MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceActor actor, MirrorPulseNamespaceExecutionSession execution)
{
    internal ValueTask<MirrorPulseDirectoryMoveProjectionReadback> ApplyAsync(MirrorPulseJournalDirectoryMoveAdmission admission,
        MirrorPulseJournalDirectoryMoveProjection projection, CancellationToken token) => actor.RunAsync(async admitted =>
    {
        actor.AssertAccess();
        var original = projection.Original;
        var root = router.GetRegistration(admission.InstanceId, admission.RootKey);
        if (root.RootId != admission.RootId || root.State != RootRegistrationState.Active ||
            !MirrorPulsePlaceholderIdentity.BelongsToRoot(root, projection.TargetIdentity) ||
            projection.TargetIdentity.RemoteRevision != original.OriginalRevision)
            throw new MirrorPulseMutationAmbiguousException("The directory Move projection lost its original active root or baseline.");
        var member = await catalog.ReadNamespaceMemberAsync(original.MemberId, admitted).ConfigureAwait(false)
            ?? throw new MirrorPulseMutationAmbiguousException("The directory Move projection lost its original member.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, admitted).ConfigureAwait(false);
        if (member.RootId != admission.RootId || member.LocalObject != original.Binding || member.IsDirectory != original.IsDirectory ||
            location.RelativePath != original.TargetPath || location.CurrentPositionOperationId != admission.LocalMoveOperationId)
            throw new MirrorPulseMutationAmbiguousException("The directory Move projection cannot adopt a later position or native object.");
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, admitted).ConfigureAwait(false);
        if (member.OwnerSid != execution.OwnerSid.Value || proof.RoleSid != execution.RoleSid.Value)
            throw new MirrorPulseMutationAmbiguousException("The directory Move projection requires its verified protected owner.");

        CloudItem item = original.IsDirectory ? fileSystem.GetDirectory(original.TargetPath) : fileSystem.GetFile(original.TargetPath);
        byte[] previous = new CloudPlaceholderIdentity(original.OriginalItemId, original.OriginalRemoteId, original.OriginalNativeRevision).Encode();
        byte[] target = projection.TargetIdentity.Encode();
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, admitted).ConfigureAwait(false);
            var before = await item.InspectAsync(admitted).ConfigureAwait(false);
            RequireSameObject(before, await lease.InspectAsync(admitted).ConfigureAwait(false), member, proof, original);
            bool nativeMatches = before.PlaceholderIdentity.Span.SequenceEqual(target);
            if (!nativeMatches && !before.PlaceholderIdentity.Span.SequenceEqual(previous))
                throw new MirrorPulseMutationAmbiguousException("The moved placeholder has a different current identity.");
            var official = await ReadOfficialProjectionAsync(original, projection.TargetIdentity, before.Kind, admitted).ConfigureAwait(false);
            if (!official.Target && !official.Original)
                throw new MirrorPulseMutationAmbiguousException("The moved placeholder has a different official projection.");
            if (!nativeMatches || !official.Target)
            {
                // The native patch must never confirm dirty bytes or request hydration.
                if (item is CloudDirectory directory)
                    await directory.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentity(projection.TargetIdentity).Build(), admitted).ConfigureAwait(false);
                else
                    await ((CloudFile)item).UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentity(projection.TargetIdentity).Build(), admitted).ConfigureAwait(false);
            }
            CancellationToken durable = CancellationToken.None;
            var after = await item.InspectAsync(durable).ConfigureAwait(false);
            RequireSameObject(after, await lease.InspectAsync(durable).ConfigureAwait(false), member, proof, original);
            if (!after.PlaceholderIdentity.Span.SequenceEqual(target) || after.ContentAvailability != before.ContentAvailability ||
                after.OnDiskDataSize != before.OnDiskDataSize || after.SynchronizationState != before.SynchronizationState)
                throw new InvalidDataException("The directory Move identity patch changed content state or failed to retain its exact target.");
            if (!(await ReadOfficialProjectionAsync(original, projection.TargetIdentity, after.Kind, durable).ConfigureAwait(false)).Target)
                throw new InvalidDataException("The public identity patch did not retain the exact official target projection.");
        }).ConfigureAwait(false);
        return new MirrorPulseDirectoryMoveProjectionReadback(
            new(original.MemberId, projection.TargetIdentity.ItemId, projection.TargetIdentity.RemoteId,
                projection.TargetIdentity.RemoteRevision), DateTimeOffset.UtcNow);
    }, token);

    internal MirrorPulseDirectoryMovePermissionIdentity PermissionIdentity(MirrorPulseJournalDirectoryMoveProjection projection) =>
        new(projection, token => ReadOfficialProjectionAsync(projection.Original, projection.TargetIdentity,
            projection.Original.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File, token));

    private async ValueTask<(bool Original, bool Target)> ReadOfficialProjectionAsync(MirrorPulseJournalDirectoryMoveMember original,
        CloudPlaceholderIdentity target, CloudItemKind kind, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var prior = await transaction.Items.GetByItemIdAsync(original.OriginalItemId, token).ConfigureAwait(false);
        var current = await transaction.Items.GetByItemIdAsync(target.ItemId, token).ConfigureAwait(false);
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        bool originalMatches = MatchesPath(prior) && prior!.RemoteId == original.OriginalRemoteId &&
            prior.RemoteRevision == original.OriginalRevision;
        bool targetMatches = MatchesPath(current) && current!.RemoteId == target.RemoteId &&
            current.RemoteRevision == target.RemoteRevision;
        if (current is not null && !targetMatches || prior is not null && prior.ItemId != target.ItemId && (!originalMatches || targetMatches))
            return default;
        return (originalMatches, targetMatches);

        bool MatchesPath(CloudItemState? row) => row is { IsTombstone: false } &&
            row.Kind == kind && row.RelativePath.Replace('\\', '/') == original.TargetPath;
    }

    private static void RequireSameObject(CloudItemSnapshot snapshot, MirrorPulseNamespacePermissionObject facts,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceProtectionProof proof, MirrorPulseJournalDirectoryMoveMember original)
    {
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != original.Binding ||
            facts.LocalObject != original.Binding || facts.RelativePath != original.TargetPath || facts.OwnerSid != member.OwnerSid ||
            facts.RootId != member.RootId || facts.IsDirectory != original.IsDirectory || facts.LinkCount != 1 ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new MirrorPulseMutationAmbiguousException("The directory Move identity patch requires the same protected native object.");
    }
}
