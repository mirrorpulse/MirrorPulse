using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private async Task<MirrorPulseNamespaceMemberMoveCompletion> SettleDirectoryMoveAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot, CloudDirectoryMoveReconciliationResult publicResult,
        MirrorPulseNamespaceProtectionProof childProof, MirrorPulseNamespaceProtectionProof sourceProof, MirrorPulseNamespaceProtectionProof targetProof,
        MirrorPulseWindowsNamespacePermissionLease sourceLease, MirrorPulseWindowsNamespacePermissionLease targetLease,
        CloudDirectory sourceParent, CloudDirectory targetParent, MirrorPulseNamespacePermissionObject sourceBefore,
        MirrorPulseNamespacePermissionObject targetBefore, CancellationToken token)
    {
        var observations = new Dictionary<Guid, MirrorPulseNamespaceMemberMoveObservation>();
        await using (var objects = await OpenDirectoryMoveObjectsAsync(snapshot.Entries, intent, atTarget: true, token).ConfigureAwait(false))
        {
            var parents = objects.Objects.ToDictionary(item => item.Entry.MemberId, item => item.Facts);
            var parent = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
            foreach (var item in objects.Objects)
            {
                var identity = CloudPlaceholderIdentity.Decode(item.Snapshot.PlaceholderIdentity.Span);
                observations.Add(item.Entry.MemberId, new(1, intent.OperationId, identity.ItemId, identity.RemoteId, identity.RemoteRevision,
                    DirectoryMoveMemberPath(item.Entry, intent, atTarget: true), item.Entry.IsDirectory, true, item.Facts.LinkCount!.Value,
                    Verification(item.Facts), Verification(item.Entry.Depth == 0 ? parent : parents[item.Entry.ParentMemberId!.Value])));
            }
            await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: true, token).ConfigureAwait(false);
        }
        var rootNative = await _catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, token).ConfigureAwait(false);
        var freshRoot = observations[intent.MemberId];
        if (rootNative is null)
            rootNative = await _catalog.RecordNamespaceMemberMoveObservationAsync(freshRoot, token).ConfigureAwait(false);
        else if (rootNative.ItemId != freshRoot.ItemId || rootNative.RemoteId != freshRoot.RemoteId ||
            rootNative.NativeRemoteRevision != freshRoot.NativeRemoteRevision || !SameDirectoryVerification(rootNative.Child, freshRoot.Child) ||
            !SameDirectoryVerification(rootNative.Parent, freshRoot.Parent))
            throw new InvalidDataException("Fresh directory recovery disagrees with the original root native observation.");
        observations[intent.MemberId] = rootNative;
        await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation, token).ConfigureAwait(false);
        var rootCandidate = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
        RequireDirectoryMemberProjection(rootCandidate, snapshot.Entries[0], intent, atTarget: true, allowOriginal: false);
        var rootProjection = await _catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, token).ConfigureAwait(false);
        if (rootProjection is null)
            rootProjection = await _catalog.RecordNamespaceMemberMoveProjectionAsync(new(1, intent.OperationId, intent.ItemId, intent.RemoteId,
                rootCandidate.RemoteRevision, intent.Target.RelativePath, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        else if (rootProjection.ProjectedRemoteRevision != rootCandidate.RemoteRevision)
            throw new InvalidDataException("Fresh directory recovery disagrees with the original root projection.");
        await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection, token).ConfigureAwait(false);
        var publicFacts = new MirrorPulseNamespaceDirectoryMovePublicResult(
            publicResult.Outcome == CloudDirectoryMoveReconciliationOutcome.Projected ? MirrorPulseNamespaceDirectoryMovePublicOutcome.Projected :
                MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected,
            publicResult.NativeMoveObserved, publicResult.DurableProjectionCommitted, publicResult.RequiresFullRescan,
            publicResult.DurableStateEntriesUpdated, DateTimeOffset.UtcNow);
        var projections = new Dictionary<Guid, MirrorPulseNamespaceMemberMoveProjection> { [intent.MemberId] = rootProjection };
        foreach (var entry in snapshot.Entries.Skip(1))
        {
            var candidate = await ReadProjectionAsync(entry.MemberId, token).ConfigureAwait(false);
            RequireDirectoryMemberProjection(candidate, entry, intent, atTarget: true, allowOriginal: false);
            projections.Add(entry.MemberId, new(1, intent.OperationId, entry.Identity.ItemId!.Value, entry.Identity.RemoteId!,
                candidate.RemoteRevision, DirectoryMoveMemberPath(entry, intent, atTarget: true), DateTimeOffset.UtcNow));
        }
        await using var final = await OpenDirectoryMoveObjectsAsync(snapshot.Entries, intent, atTarget: true, token).ConfigureAwait(false);
        var finalParents = final.Objects.ToDictionary(item => item.Entry.MemberId, item => item.Facts);
        var finalParent = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
        await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, token).ConfigureAwait(false);
        await ReinspectDirectoryMoveObjectsAsync(final, intent, atTarget: true, token).ConfigureAwait(false);
        _ = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(token).ConfigureAwait(false));
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(token).ConfigureAwait(false));
        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        var entries = new List<MirrorPulseNamespaceDirectoryMoveMemberCompletion>(final.Objects.Count);
        foreach (var item in final.Objects)
        {
            var native = observations[item.Entry.MemberId];
            if (CloudPlaceholderIdentity.Decode(item.Snapshot.PlaceholderIdentity.Span).RemoteRevision != native.NativeRemoteRevision)
                throw new InvalidDataException("A directory member native identity changed during settlement.");
            var projection = projections[item.Entry.MemberId];
            Guid parentId = item.Entry.Depth == 0 ? intent.Target.ParentMemberId : item.Entry.ParentMemberId!.Value;
            var completion = new MirrorPulseNamespaceMemberMoveCompletion(1, intent.OperationId, native.ItemId, native.RemoteId,
                native.RelativePath, item.Entry.IsDirectory, verifiedAt, Verification(item.Facts),
                Verification(item.Entry.Depth == 0 ? finalParent : finalParents[parentId]), projection.ProjectedRemoteRevision);
            bool legacy = item.Entry.PreviousPositionOperationId is null && item.Entry.PreviousMoveOperationId is { } previous &&
                await _catalog.ReadNamespaceMemberPositionAsync(item.Entry.MemberId, previous, token).ConfigureAwait(false) is null;
            entries.Add(new(item.Entry.MemberId, parentId, native, projection, completion, legacy));
        }
        var settled = await _catalog.CompleteNamespaceDirectoryMoveAsync(new(1, intent.OperationId, snapshot.PublicEvidenceSha256,
            publicFacts, entries[0].Completion, entries.AsReadOnly()), token).ConfigureAwait(false);
        return settled.Root;
    }

    private static bool SameDirectoryVerification(MirrorPulseNamespacePermissionVerification first,
        MirrorPulseNamespacePermissionVerification fresh) => first.LocalObject == fresh.LocalObject &&
            first.OwnerSid == fresh.OwnerSid && first.Dacl == fresh.Dacl;
}
