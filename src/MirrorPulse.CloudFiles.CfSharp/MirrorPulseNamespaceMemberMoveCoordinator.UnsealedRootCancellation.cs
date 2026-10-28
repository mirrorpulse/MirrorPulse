using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Cancels a public preparation without manufacturing a sealed namespace move or adopting its role.</summary>
    internal async ValueTask<MirrorPulseRootLabelUnsealedCancellation> CancelUnsealedRootLabelAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label preparation is required.", nameof(operationId));
        return await _actor.RunAsync(token => CancelUnsealedRootLabelAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseRootLabelUnsealedCancellation> CancelUnsealedRootLabelAdmittedAsync(Guid operationId, CancellationToken token)
    {
        _actor.AssertAccess();
        var stage = MirrorPulseRootLabelCancellationStage.OriginalOwnership;
        try
        {
            var replay = await _catalog.ReadUnsealedRootLabelCancellationAsync(operationId, token).ConfigureAwait(false);
            if (replay is not null) return replay;
            var history = await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original public Label preparation is missing.");
            if (history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared || history.Observation is not null ||
                await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Unsealed cancellation cannot replace a started, sealed or completed original.");
            var root = (await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(item => item.RootId == history.Intent.RootId);
            if (root.State is RootRegistrationState.Active or RootRegistrationState.Disabled)
                _router.RefreshLocalCommandLifecycleRegistration(root);
            if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) || root.DirectoryName != history.Intent.SourceName ||
                _router.FindRegistration(root.RootId) != root)
                throw new InvalidOperationException("Unsealed cancellation requires its unchanged original source route.");
            var proof = MirrorPulseManagedRootRenameCoordinator.Decode(history, root);
            var member = await _catalog.ReadNamespaceMemberByBindingAsync(history.Proof!.LocalObject, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The public preparation lost its registered original root.");
            var location = await _catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
            if (!member.IsDirectory || member.RootId != root.RootId || member.OwnerSid != _execution.OwnerSid.Value ||
                location.RelativePath != history.Intent.SourceName || location.ParentMemberId is not { } anchorMember)
                throw new InvalidDataException("The original preparation no longer names its protected current-user root and direct anchor.");
            stage = MirrorPulseRootLabelCancellationStage.MetadataInventory;
            var cancellationTree = await _catalog.ReadRootLabelCancellationTreeAsync(operationId, token).ConfigureAwait(false);
            if (cancellationTree.RootMemberId != member.MemberId) throw new InvalidDataException("Cancellation lost its original root inventory.");
            var entries = RootCancellationEntries(cancellationTree);
            stage = MirrorPulseRootLabelCancellationStage.AnchorMetadata;
            var anchorSnapshot = await ReadUnsealedCancellationAnchorAsync(anchorMember, member, token).ConfigureAwait(false);
            if (anchorSnapshot != cancellationTree.Anchor) throw new InvalidOperationException("Cancellation lost its coherent current anchor metadata.");
            var anchorProof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(anchorSnapshot);
            MirrorPulseRootLabelUnsealedCancellation cancelled = null!;
            await _execution.RunNormalUserOperationAsync(async () =>
            {
                using var caller = WindowsIdentity.GetCurrent();
                if (caller.User?.Value != member.OwnerSid) throw new UnauthorizedAccessException("Unsealed cancellation requires the original current-user owner.");
                stage = MirrorPulseRootLabelCancellationStage.AnchorInspection;
                await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(_fileSystem.Root, _router, token).ConfigureAwait(false);
                RequireUnsealedCancellationObject(await anchor.InspectAsync(token).ConfigureAwait(false), await _fileSystem.Root.InspectAsync(token).ConfigureAwait(false),
                    anchorProof, anchorSnapshot.Context.Member.LocalObject, member.OwnerSid, null, "", true);
                await RequireOriginalRootSpellingAsync(history.Intent.SourceName, token).ConfigureAwait(false);
                stage = MirrorPulseRootLabelCancellationStage.MemberInspection;
                await using var objects = await OpenRootCancellationObjectsAsync(entries, cancellationTree, root.RootId, member.OwnerSid, null, token).ConfigureAwait(false);
                var rootObject = objects.Objects.Single(item => item.Entry.MemberId == member.MemberId);
                stage = MirrorPulseRootLabelCancellationStage.OriginalRootIdentity;
                MirrorPulseRootLabelCancellationIdentity.RequireOriginal(proof.RootItemId, rootObject.Snapshot.ItemId,
                    rootObject.Snapshot.PlaceholderIdentity.Span, proof.ExpectedPlaceholderIdentity.Span);
                stage = MirrorPulseRootLabelCancellationStage.PublicReconciliation;
                var unmoved = await _fileSystem.GetDirectory(proof.SourceRelativePath).ReconcileMoveAsync(proof, token).ConfigureAwait(false);
                if (unmoved.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || unmoved.NativeMoveObserved || unmoved.DurableProjectionCommitted ||
                    unmoved.RequiresFullRescan || unmoved.DurableStateEntriesUpdated != 0 || unmoved.Error is not null || unmoved.NativeHResult is not null)
                    throw new InvalidOperationException("Unsealed cancellation requires actual public NotMoved without progress or uncertainty.");
                DateTimeOffset publicAt = DateTimeOffset.UtcNow;
                stage = MirrorPulseRootLabelCancellationStage.MemberReinspection;
                await ReinspectRootCancellationObjectsAsync(objects, cancellationTree, root.RootId, member.OwnerSid, null, token).ConfigureAwait(false);
                stage = MirrorPulseRootLabelCancellationStage.FinalNativeObservations;
                var observations = new List<MirrorPulseRootLabelCancellationMember>(entries.Length);
                foreach (var item in objects.Objects)
                {
                    var current = item.Proof;
                    var facts = await item.Lease.InspectAsync(token).ConfigureAwait(false);
                    var snapshot = await item.Item.InspectAsync(token).ConfigureAwait(false);
                    RequireUnsealedCancellationObject(facts, snapshot, current, item.Entry.LocalObject, member.OwnerSid,
                        root.RootId, item.Entry.RelativePath, item.Entry.IsDirectory);
                    MirrorPulseNamespaceBirthProtectionVerifier.RequireRegisteredPublicIdentity(snapshot, item.Entry.Identity, requirePlaceholder: true);
                    observations.Add(new(item.Entry.MemberId, current.ProtectionId, new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt)));
                }
                await RequireExactLocalDirectoryTreeAsync(entries, entry => entry.RelativePath, token).ConfigureAwait(false);
                await RequireOriginalRootSpellingAsync(history.Intent.SourceName, token).ConfigureAwait(false);
                if (await ReadUnsealedCancellationAnchorAsync(anchorMember, member, token).ConfigureAwait(false) != anchorSnapshot)
                    throw new InvalidOperationException("Unsealed cancellation anchor protection changed during guarded inspection.");
                var finalAnchor = await anchor.InspectAsync(token).ConfigureAwait(false);
                RequireUnsealedCancellationObject(finalAnchor, await _fileSystem.Root.InspectAsync(token).ConfigureAwait(false), anchorProof,
                    anchorSnapshot.Context.Member.LocalObject, member.OwnerSid, null, "", true);
                string hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(history.Proof.DirectoryMoveEvidence!)));
                var observation = new MirrorPulseRootLabelCancellation(1, operationId, root.RootId, hash, publicAt, DateTimeOffset.UtcNow,
                    false, false, false, 0, anchorMember, anchorProof.ProtectionId,
                    new(finalAnchor.LocalObject, finalAnchor.OwnerSid, finalAnchor.Dacl, finalAnchor.ObservedAt), observations.AsReadOnly());
                stage = MirrorPulseRootLabelCancellationStage.CatalogCancellation;
                cancelled = await _catalog.CancelUnsealedRootLabelAsync(new(cancellationTree.RecoveryOwner is null ? 1 : 2, member.MemberId, observation,
                    new(1, operationId, hash, entries))
                { RecoveryOwner = cancellationTree.RecoveryOwner }, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return cancelled;
        }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            MirrorPulseRootLabelCancellationFailureDiagnostics.MarkStage(original, stage);
            throw;
        }
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadUnsealedCancellationAnchorAsync(Guid anchorMember,
        MirrorPulseNamespaceMemberRegistration root, CancellationToken token)
    {
        var snapshot = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchorMember, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Unsealed cancellation lost its registered physical anchor.");
        var member = snapshot.Context.Member; var location = snapshot.Context.Location;
        if (member.ParentMemberId is not null || member.RootId is not null || !member.IsDirectory || member.RelativePath.Length != 0 ||
            location.ParentMemberId is not null || location.RelativePath.Length != 0 || member.OwnerSid != root.OwnerSid ||
            member.LocalObject.VolumeSerialNumber != root.LocalObject.VolumeSerialNumber ||
            member.LocalObject.LocalFileId != root.LocalObject.SyncRootFileId || member.LocalObject.SyncRootFileId != root.LocalObject.SyncRootFileId)
            throw new InvalidDataException("Unsealed cancellation cannot replace the original physical anchor.");
        return snapshot;
    }

    private static void RequireUnsealedCancellationObject(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceProtectionProof proof, MirrorPulseLocalFileBinding binding, string owner, RootId? root, string path, bool directory)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } actual ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(actual.VolumeSerialNumber, actual.SyncRootFileId, actual.LocalFileId) ||
            facts.LocalObject != binding || facts.LocalObject != proof.Verification.LocalObject || facts.OwnerSid != owner || proof.OwnerSid != owner ||
            facts.RootId != root || proof.RootId != root || facts.RelativePath != path || proof.RelativePath != path ||
            facts.IsDirectory != directory || proof.IsDirectory != directory || (snapshot.Kind == CloudItemKind.Directory) != directory || facts.LinkCount != 1 ||
            facts.Dacl != proof.Verification.Dacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(proof.ExpectedDacl, facts.Dacl))
            throw ObserveCancellationObjectFailure(new InvalidDataException("Unsealed cancellation lost its actual retained binding, owner, path, kind, link count or verified descriptor."),
                facts, snapshot, proof, binding, owner, root, path, directory);
    }
}
