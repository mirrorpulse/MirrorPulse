using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private async Task<MirrorPulseNamespaceMemberMoveCompletion> SettleRootLabelAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot, CloudDirectoryMoveReconciliationResult publicResult, CloudDirectoryMoveProof proof,
        MirrorPulseNamespaceProtectionProof childProof, MirrorPulseNamespaceProtectionProof anchorProof,
        MirrorPulseWindowsNamespacePermissionLease anchor, MirrorPulseNamespacePermissionObject anchorBefore,
        MirrorPulseRootLabelTargetInspection inspection, CancellationToken token)
    {
        var observations = new Dictionary<Guid, MirrorPulseNamespaceMemberMoveObservation>();
        await using (var objects = await OpenRootLabelObjectsAsync(snapshot.Entries, intent, atTarget: true, inspection, token).ConfigureAwait(false))
        {
            var parents = objects.Objects.ToDictionary(item => item.Entry.MemberId, item => item.Facts);
            var parent = await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, token).ConfigureAwait(false);
            var actualRoot = objects.Objects[0];
            if (!actualRoot.Snapshot.PlaceholderIdentity.Span.SequenceEqual(proof.ExpectedPlaceholderIdentity.Span))
                throw new InvalidDataException("The moved root does not retain its complete original public identity.");
            var history = await _catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Root Label native observation lost its original history.");
            string actualIdentity = Convert.ToBase64String(actualRoot.Snapshot.PlaceholderIdentity.Span);
            if (history.Observation is null)
                await _catalog.ObserveManagedRootRenameAsync(intent.OperationId,
                    new(actualRoot.Facts.LocalObject, actualIdentity, intent.Target.RelativePath, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            else if (history.Observation.LocalObject != actualRoot.Facts.LocalObject || history.Observation.PlaceholderIdentity != actualIdentity ||
                history.Observation.DirectoryName != intent.Target.RelativePath)
                throw new InvalidDataException("Fresh root Label recovery disagrees with the original independent native observation.");
            foreach (var item in objects.Objects)
            {
                var identity = CloudPlaceholderIdentity.Decode(item.Snapshot.PlaceholderIdentity.Span);
                observations.Add(item.Entry.MemberId, new(item.Entry.Depth == 0 ? 2 : 1, intent.OperationId,
                    identity.ItemId, identity.RemoteId, identity.RemoteRevision, DirectoryMoveMemberPath(item.Entry, intent, atTarget: true),
                    item.Entry.IsDirectory, true, item.Facts.LinkCount!.Value, Verification(item.Facts),
                    Verification(item.Entry.Depth == 0 ? parent : parents[item.Entry.ParentMemberId!.Value])));
            }
            await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: true, token).ConfigureAwait(false);
        }
        var rootNative = await _catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, token).ConfigureAwait(false);
        var freshRoot = observations[intent.MemberId];
        if (rootNative is null)
            rootNative = await _catalog.RecordNamespaceMemberMoveObservationAsync(freshRoot, token).ConfigureAwait(false);
        else if (rootNative.Version != 2 || rootNative.ItemId != freshRoot.ItemId || rootNative.RemoteId != freshRoot.RemoteId ||
            rootNative.NativeRemoteRevision != freshRoot.NativeRemoteRevision || !SameDirectoryVerification(rootNative.Child, freshRoot.Child) ||
            !SameDirectoryVerification(rootNative.Parent, freshRoot.Parent))
            throw new InvalidDataException("Fresh root Label recovery disagrees with its original native member receipt.");
        observations[intent.MemberId] = rootNative;
        await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation, token).ConfigureAwait(false);
        var rootCandidate = await ReadProjectionAsync(intent.MemberId, token).ConfigureAwait(false);
        RequireDirectoryMemberProjection(rootCandidate, snapshot.Entries[0], intent, atTarget: true, allowOriginal: false);
        var rootProjection = await _catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, token).ConfigureAwait(false);
        if (rootProjection is null)
            rootProjection = await _catalog.RecordNamespaceMemberMoveProjectionAsync(new(2, intent.OperationId, intent.ItemId, intent.RemoteId,
                rootCandidate.RemoteRevision, intent.Target.RelativePath, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        else if (rootProjection.Version != 2 || rootProjection.ProjectedRemoteRevision != rootCandidate.RemoteRevision)
            throw new InvalidDataException("Fresh root Label recovery disagrees with its original official projection.");
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
        await using var final = await OpenRootLabelObjectsAsync(snapshot.Entries, intent, atTarget: true, inspection, token).ConfigureAwait(false);
        var finalParents = final.Objects.ToDictionary(item => item.Entry.MemberId, item => item.Facts);
        var finalParent = await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, token).ConfigureAwait(false);
        await RequireRootLabelAdmissionAsync(intent, childProof, anchorProof, token).ConfigureAwait(false);
        await ReinspectDirectoryMoveObjectsAsync(final, intent, atTarget: true, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(anchorBefore,
            await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, token).ConfigureAwait(false));
        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        var entries = new List<MirrorPulseNamespaceDirectoryMoveMemberCompletion>(final.Objects.Count);
        foreach (var item in final.Objects)
        {
            var native = observations[item.Entry.MemberId];
            if (CloudPlaceholderIdentity.Decode(item.Snapshot.PlaceholderIdentity.Span).RemoteRevision != native.NativeRemoteRevision)
                throw new InvalidDataException("A root Label subtree identity changed during final settlement.");
            var projection = projections[item.Entry.MemberId];
            Guid parentId = item.Entry.Depth == 0 ? intent.Target.ParentMemberId : item.Entry.ParentMemberId!.Value;
            var completion = new MirrorPulseNamespaceMemberMoveCompletion(item.Entry.Depth == 0 ? 2 : 1,
                intent.OperationId, native.ItemId, native.RemoteId, native.RelativePath, item.Entry.IsDirectory, verifiedAt,
                Verification(item.Facts), Verification(item.Entry.Depth == 0 ? finalParent : finalParents[parentId]), projection.ProjectedRemoteRevision);
            bool legacy = item.Entry.PreviousPositionOperationId is null && item.Entry.PreviousMoveOperationId is { } previous &&
                await _catalog.ReadNamespaceMemberPositionAsync(item.Entry.MemberId, previous, token).ConfigureAwait(false) is null;
            entries.Add(new(item.Entry.MemberId, parentId, native, projection, completion, legacy));
        }
        var settled = await _catalog.CompleteNamespaceDirectoryMoveAsync(new(1, intent.OperationId, snapshot.PublicEvidenceSha256,
            publicFacts, entries[0].Completion, entries.AsReadOnly()), token).ConfigureAwait(false);
        return settled.Root;
    }
}
