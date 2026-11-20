using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Cancels only the original sealed Label before any durable native start.</summary>
    /// <remarks>The Host owns root/instance/library quiescence. This never adopts the old role or grants readiness.</remarks>
    internal async ValueTask<MirrorPulseRootLabelCancellation> CancelRootLabelAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original root Label operation is required.", nameof(operationId));
        return await _actor.RunAsync(token => CancelRootLabelAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseRootLabelCancellation> CancelRootLabelAdmittedAsync(Guid operationId, CancellationToken token)
    {
        _actor.AssertAccess();
        var intent = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Cancellation requires the original sealed root Label admission.");
        if (intent.Version != 3 || intent.RemoteEntry is not null || intent.OwnerSid != _execution.OwnerSid.Value)
            throw new InvalidOperationException("Cancellation requires the original local Label and current-user owner.");
        var replay = await _catalog.ReadRootLabelCancellationAsync(operationId, token).ConfigureAwait(false);
        if (replay is not null) return replay;
        intent = await _catalog.ValidateUnstartedRootLabelCancellationAsync(operationId, token).ConfigureAwait(false);
        if (await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false) is not null ||
            await _catalog.ReadNamespaceMemberMoveObservationAsync(operationId, token).ConfigureAwait(false) is not null ||
            await _catalog.ReadNamespaceMemberMoveProjectionAsync(operationId, token).ConfigureAwait(false) is not null ||
            await _catalog.ReadNamespaceDirectoryMoveCompletionAsync(operationId, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A started or observed original root Label cannot be cancelled.");
        var history = await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation lost the original root Label history.");
        var registration = (await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(root => root.RootId == intent.RootId);
        if (history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared || history.Observation is not null ||
            registration.DirectoryName != intent.Source.RelativePath || _router.FindRegistration(intent.RootId) != registration)
            throw new InvalidOperationException("Cancellation requires the unchanged original source mapping.");
        var publicProof = MirrorPulseManagedRootRenameCoordinator.Decode(history, registration);
        RequireDirectoryMoveProof(publicProof, intent);
        var snapshot = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation lost the original complete sealed tree.");
        var cancellationTree = await _catalog.ReadRootLabelCancellationTreeAsync(operationId, token).ConfigureAwait(false);
        var anchorSnapshot = await ReadRootLabelCancellationAnchorAsync(intent, token).ConfigureAwait(false);
        if (anchorSnapshot != cancellationTree.Anchor) throw new InvalidOperationException("Cancellation lost its coherent current anchor metadata.");
        var anchorProof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(anchorSnapshot);
        MirrorPulseRootLabelCancellation cancelled = null!;
        // Metadata-only reads under the normal owner cannot acquire either the
        // original execution role or permission-write authority after a restart.
        await _execution.RunNormalUserOperationAsync(async () =>
        {
            using var caller = WindowsIdentity.GetCurrent();
            if (caller.User?.Value != intent.OwnerSid)
                throw new UnauthorizedAccessException("Cancellation requires the original current-user owner.");
            await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(_fileSystem.Root, _router, token).ConfigureAwait(false);
            RequireRootLabelCancellationAnchor(await anchor.InspectAsync(token).ConfigureAwait(false), anchorProof, intent);
            await RequireOriginalRootSpellingAsync(intent.Source.RelativePath, token).ConfigureAwait(false);
            await using var objects = await OpenRootCancellationObjectsAsync(snapshot.Entries, cancellationTree, intent.RootId, intent.OwnerSid, intent, token).ConfigureAwait(false);
            if (!objects.Objects[0].Snapshot.PlaceholderIdentity.Span.SequenceEqual(publicProof.ExpectedPlaceholderIdentity.Span))
                throw new InvalidDataException("Cancellation lost its original complete public root identity.");
            foreach (var item in objects.Objects)
                RequireDirectoryMemberProjection(await ReadProjectionAsync(item.Entry.MemberId, token).ConfigureAwait(false),
                    item.Entry, intent, atTarget: false, allowOriginal: false);
            var unmoved = await _fileSystem.GetDirectory(intent.Source.RelativePath).ReconcileMoveAsync(publicProof, token).ConfigureAwait(false);
            if (unmoved.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || unmoved.NativeMoveObserved ||
                unmoved.DurableProjectionCommitted || unmoved.RequiresFullRescan || unmoved.DurableStateEntriesUpdated != 0 ||
                unmoved.Error is not null || unmoved.NativeHResult is not null)
                throw new InvalidOperationException("Cancellation requires an actual successful public NotMoved result without progress or uncertainty.");
            DateTimeOffset publicAt = DateTimeOffset.UtcNow;
            await ReinspectRootCancellationObjectsAsync(objects, cancellationTree, intent.RootId, intent.OwnerSid, intent, token).ConfigureAwait(false);
            await RequireOriginalRootSpellingAsync(intent.Source.RelativePath, token).ConfigureAwait(false);
            if (await ReadRootLabelCancellationAnchorAsync(intent, token).ConfigureAwait(false) != anchorSnapshot)
                throw new InvalidOperationException("Cancellation anchor protection changed during guarded inspection.");
            var finalAnchor = await anchor.InspectAsync(token).ConfigureAwait(false);
            RequireRootLabelCancellationAnchor(finalAnchor, anchorProof, intent);
            var observations = new List<MirrorPulseRootLabelCancellationMember>(objects.Objects.Count);
            foreach (var item in objects.Objects)
            {
                var facts = await item.Lease.InspectAsync(token).ConfigureAwait(false);
                RequireDirectoryMoveObject(item.Entry, item.Entry.RelativePath, facts,
                    await item.Item.InspectAsync(token).ConfigureAwait(false), item.Proof, intent);
                observations.Add(new(item.Entry.MemberId, item.Proof.ProtectionId,
                    new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt)));
            }
            await RequireExactLocalDirectoryTreeAsync(snapshot.Entries, intent, atTarget: false, token).ConfigureAwait(false);
            cancelled = await _catalog.CancelUnstartedRootLabelAsync(new(1, operationId, intent.RootId, snapshot.PublicEvidenceSha256,
                publicAt, DateTimeOffset.UtcNow, false, false, false, 0, anchorSnapshot.Context.Member.MemberId, anchorProof.ProtectionId,
                new(finalAnchor.LocalObject, finalAnchor.OwnerSid, finalAnchor.Dacl, finalAnchor.ObservedAt), observations.AsReadOnly()), token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return cancelled;
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadRootLabelCancellationAnchorAsync(
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        var snapshot = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(intent.Source.ParentMemberId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation lost its registered physical sync-root anchor.");
        var member = snapshot.Context.Member; var location = snapshot.Context.Location;
        if (member.ParentMemberId is not null || member.RootId is not null || !member.IsDirectory || member.RelativePath.Length != 0 ||
            location.ParentMemberId is not null || location.RelativePath.Length != 0 || location.CurrentPositionOperationId != intent.Source.ParentPositionOperationId ||
            member.LocalObject != intent.Source.ParentLocalObject || member.LocalObject != intent.Target.ParentLocalObject ||
            member.LocalObject.LocalFileId != intent.LocalObject.SyncRootFileId || member.OwnerSid != intent.OwnerSid)
            throw new InvalidDataException("Cancellation cannot replace the original physical anchor, owner or position.");
        return snapshot;
    }

    private static void RequireRootLabelCancellationAnchor(MirrorPulseNamespacePermissionObject facts,
        MirrorPulseNamespaceProtectionProof proof, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        if (facts.RootId is not null || facts.RelativePath.Length != 0 || !facts.IsDirectory || facts.LinkCount != 1 ||
            facts.LocalObject != intent.Source.ParentLocalObject || facts.LocalObject != proof.Verification.LocalObject ||
            facts.OwnerSid != intent.OwnerSid || facts.OwnerSid != proof.OwnerSid || facts.Dacl != proof.Verification.Dacl ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw new InvalidDataException("Cancellation requires the freshly retained original anchor and its current verified protection.");
    }
}
