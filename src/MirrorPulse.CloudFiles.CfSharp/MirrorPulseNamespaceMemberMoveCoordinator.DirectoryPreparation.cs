using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private async Task<MirrorPulseNamespaceMemberMoveIntent> PrepareDirectoryMoveAdmittedAsync(Guid operationId,
        MirrorPulseNamespaceMemberRegistration member, Guid targetParentMemberId, string targetName, RemoteMoveRequest? remoteRequest, CancellationToken token)
    {
        _actor.AssertAccess();
        var location = await _catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        var sourceParent = await _catalog.ReadNamespaceMemberAsync(location.ParentMemberId!.Value, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory source parent is not registered.");
        var targetParent = await _catalog.ReadNamespaceMemberAsync(targetParentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The directory target parent is not registered.");
        if (sourceParent.RootId is null)
            throw new NotSupportedException("Managed first-level roots require the separate display-label operation.");
        if (!targetParent.IsDirectory || targetParent.RootId != member.RootId || targetParent.OwnerSid != member.OwnerSid)
            throw new ArgumentException("The target requires a protected directory in the same managed root.", nameof(targetParentMemberId));
        var sourceParentLocation = await _catalog.ReadNamespaceMemberLocationAsync(sourceParent.MemberId, token).ConfigureAwait(false);
        var targetParentLocation = await _catalog.ReadNamespaceMemberLocationAsync(targetParent.MemberId, token).ConfigureAwait(false);
        CloudDirectory source = _fileSystem.GetDirectory(location.RelativePath);
        CloudDirectory sourceDirectory = source.Parent ?? throw new InvalidDataException("A directory requires a source parent.");
        CloudDirectory targetDirectory = _fileSystem.GetDirectory(targetParentLocation.RelativePath);
        CloudDirectory target = targetDirectory.GetDirectory(targetName);
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, member, token).ConfigureAwait(false);
        var sourceProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, sourceParent, token).ConfigureAwait(false);
        var targetProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(_catalog, targetParent, token).ConfigureAwait(false);
        var identity = await _catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
        if (identity.ItemId is not { } itemId || identity.RemoteId is not { } remoteId)
            throw new InvalidOperationException("Directory preparation requires its retained public identity.");
        var entries = await ReadDirectoryMoveEntriesAsync(member.MemberId, token).ConfigureAwait(false);
        if (entries.Any(entry => entry.MemberId == targetParentMemberId))
            throw new ArgumentException("A controlled directory cannot move into its own subtree.", nameof(targetParentMemberId));
        var intent = new MirrorPulseNamespaceMemberMoveIntent(1, operationId, member.MemberId, childProof.ProtectionId,
            member.RootId!.Value, member.LocalObject, itemId, remoteId, member.OwnerSid, _execution.RoleSid.Value,
            new(sourceParent.MemberId, sourceParent.LocalObject, sourceProof.ProtectionId, source.RelativePath.Replace('\\', '/'))
            { ParentPositionOperationId = sourceParentLocation.CurrentPositionOperationId },
            new(targetParent.MemberId, targetParent.LocalObject, targetProof.ProtectionId, target.RelativePath.Replace('\\', '/'))
            { ParentPositionOperationId = targetParentLocation.CurrentPositionOperationId }, DateTimeOffset.UtcNow)
        {
            PreviousMoveOperationId = location.CompletedMoveOperationId,
            SourcePositionOperationId = location.PositionOperationId,
            IdentityAcceptanceOperationId = identity.AcceptanceOperationId
        };
        MirrorPulseNamespaceMemberMoveIntent prepared = null!;
        await _execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(intent);
            await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceDirectory, _router, token).ConfigureAwait(false);
            await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetDirectory, _router, token).ConfigureAwait(false);
            var sourceBefore = await InspectParentAsync(sourceDirectory, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
            var targetBefore = await InspectParentAsync(targetDirectory, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
            if (!string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                (await target.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The requested directory destination is occupied.");
            byte[] originalIdentity;
            await using (var objects = await OpenDirectoryMoveObjectsAsync(entries, intent, atTarget: false, token).ConfigureAwait(false))
            {
                foreach (var item in objects.Objects)
                    RequireDirectoryMemberProjection(await ReadProjectionAsync(item.Entry.MemberId, token).ConfigureAwait(false),
                        item.Entry, intent, atTarget: false, allowOriginal: false);
                await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: false, token).ConfigureAwait(false);
                originalIdentity = objects.Objects[0].Snapshot.PlaceholderIdentity.ToArray();
            }
            // CfSharp acquires its own metadata scope. Release our subtree handles first,
            // retaining external ancestor guards and rechecking every original object afterwards.
            var proof = await source.PrepareMoveAsync(targetDirectory, targetName, token).ConfigureAwait(false);
            RequireDirectoryMoveProof(proof, intent);
            if (!proof.ExpectedPlaceholderIdentity.Span.SequenceEqual(originalIdentity))
                throw new InvalidDataException("The public preparation captured a different complete root identity.");
            await using var verified = await OpenDirectoryMoveObjectsAsync(entries, intent, atTarget: false, token).ConfigureAwait(false);
            await ReinspectDirectoryMoveObjectsAsync(verified, intent, atTarget: false, token).ConfigureAwait(false);
            if (!await HasUnchangedProofsAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false))
                throw new InvalidOperationException("Directory protection changed during public preparation.");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(token).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(token).ConfigureAwait(false));
            _actor.AssertAccess();
            intent = intent with { DirectoryMoveEvidence = Convert.ToBase64String(proof.Encode()), PreparedAt = DateTimeOffset.UtcNow };
            intent = AttachRemoteReference(intent, remoteRequest, CloudPlaceholderIdentity.Decode(originalIdentity));
            prepared = await _catalog.PrepareNamespaceMemberMoveAsync(intent, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return prepared;
    }

    private static void RequireDirectoryMemberProjection(MirrorPulseNamespaceMemberLocationCandidate projection,
        MirrorPulseNamespaceDirectoryMoveEntry entry, MirrorPulseNamespaceMemberMoveIntent intent, bool atTarget, bool allowOriginal)
    {
        string? path = projection.RelativePath?.Replace('\\', '/');
        if (projection.State != MirrorPulseNamespaceMemberLocationState.Projected || projection.ItemId != entry.Identity.ItemId ||
            projection.RemoteId != entry.Identity.RemoteId ||
            path != DirectoryMoveMemberPath(entry, intent, atTarget) && (!allowOriginal || path != entry.RelativePath))
            throw new InvalidDataException("The official directory member lost its retained identity or admitted projection path.");
    }
}
