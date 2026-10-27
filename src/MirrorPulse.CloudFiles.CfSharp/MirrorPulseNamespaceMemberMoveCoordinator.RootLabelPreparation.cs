using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Seals current protected members for the same original Label proof, without native movement.</summary>
    /// <remarks>
    /// The caller owns root and instance admission. This internal prerequisite does not activate
    /// protected root execution in the public generic member-move entry. The Host
    /// root command supplies separate root and instance/library quiescence.
    /// </remarks>
    internal async ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareRootLabelAsync(Guid originalOperationId,
        CancellationToken cancellationToken = default)
    {
        if (originalOperationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(originalOperationId));
        return await _actor.RunAsync(token => PrepareRootLabelAdmittedAsync(originalOperationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareRootLabelAdmittedAsync(Guid operationId, CancellationToken token)
    {
        _actor.AssertAccess();
        var subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeOriginalOwnership;
        try
        {
            var history = await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original Label operation is missing.");
            var root = (await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(item => item.RootId == history.Intent.RootId);
            var publicProof = MirrorPulseManagedRootRenameCoordinator.Decode(history, root);
            var replay = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.Version != 3 || replay.RootId != root.RootId || replay.DirectoryMoveEvidence != history.Proof!.DirectoryMoveEvidence ||
                    replay.LocalObject != history.Proof.LocalObject || replay.ItemId != publicProof.RootItemId ||
                    replay.Source.RelativePath != history.Intent.SourceName || replay.Target.RelativePath != history.Intent.TargetName)
                    throw new InvalidDataException("The original Label operation cannot adopt a different sealed member move.");
                if (!history.Intent.IsPending)
                {
                    if (history.Intent.Phase != MirrorPulseRootRenamePhase.Completed ||
                        await _catalog.ReadNamespaceDirectoryMoveCompletionAsync(operationId, token).ConfigureAwait(false) is null)
                        throw new InvalidOperationException("Only a complete original subtree receipt can replay historical Label preparation.");
                    return replay;
                }
                return await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
            }
            if (history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared ||
                root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) || root.DirectoryName != history.Intent.SourceName ||
                _router.FindRegistration(root.RootId) != root)
                throw new InvalidOperationException("New subtree preparation requires the original unmoved Label and current source registration.");
            subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeProtectionInventory;
            var member = await _catalog.ReadNamespaceMemberByBindingAsync(history.Proof!.LocalObject, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original root object has no protected product membership.");
            var location = await _catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
            if (!member.IsDirectory || member.RootId != root.RootId || location.RelativePath != history.Intent.SourceName || location.ParentMemberId is null)
                throw new InvalidDataException("The original Label proof must identify the current first-level root member.");
            var anchor = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(location.ParentMemberId.Value, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original root lost its registered sync-root anchor.");
            var child = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original root lost its current protection.");
            var childProof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(child);
            var anchorProof = MirrorPulseNamespaceProtectionProofReader.FromSnapshot(anchor);
            var intent = new MirrorPulseNamespaceMemberMoveIntent(3, operationId, member.MemberId, childProof.ProtectionId,
                root.RootId, member.LocalObject, publicProof.RootItemId,
                CloudPlaceholderIdentity.Decode(publicProof.ExpectedPlaceholderIdentity.Span).RemoteId, member.OwnerSid, _execution.RoleSid.Value,
                new(anchor.Context.Member.MemberId, anchor.Context.Member.LocalObject, anchorProof.ProtectionId, history.Intent.SourceName)
                { ParentPositionOperationId = anchor.Context.Location.CurrentPositionOperationId },
                new(anchor.Context.Member.MemberId, anchor.Context.Member.LocalObject, anchorProof.ProtectionId, history.Intent.TargetName)
                { ParentPositionOperationId = anchor.Context.Location.CurrentPositionOperationId }, DateTimeOffset.UtcNow)
            {
                DirectoryMoveEvidence = history.Proof.DirectoryMoveEvidence,
                PreviousMoveOperationId = location.CompletedMoveOperationId,
                SourcePositionOperationId = location.PositionOperationId,
                IdentityAcceptanceOperationId = child.Identity.AcceptanceOperationId
            };
            anchorProof = MirrorPulseRootLabelAnchorInspection.RequireProof(anchor, intent);
            RequireDirectoryMoveProof(publicProof, intent);
            subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeMemberInventory;
            var entries = await ReadDirectoryMoveEntriesAsync(member.MemberId, token).ConfigureAwait(false);
            MirrorPulseNamespaceMemberMoveIntent prepared = null!;
            subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeAnchorInspection;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                RequireCaller(intent);
                await using var parent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(_fileSystem.Root, _router, token).ConfigureAwait(false);
                var before = await parent.InspectAsync(token).ConfigureAwait(false);
                MirrorPulseRootLabelAnchorInspection.RequireNative(before, await _fileSystem.Root.InspectAsync(token).ConfigureAwait(false), anchorProof, intent);
                await RequireOriginalRootSpellingAsync(intent.Source.RelativePath, token).ConfigureAwait(false);
                if (!string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    (await _fileSystem.GetDirectory(intent.Target.RelativePath).InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new IOException("The original Label target is occupied.");
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreePublicReconciliation;
                var unmoved = await _fileSystem.GetDirectory(intent.Source.RelativePath).ReconcileMoveAsync(publicProof, token).ConfigureAwait(false);
                if (unmoved.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || unmoved.NativeMoveObserved ||
                    unmoved.DurableProjectionCommitted || unmoved.RequiresFullRescan)
                    throw new InvalidOperationException("New root subtree admission requires public authentication of the original unmoved preparation.");
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeMemberInspection;
                await using var objects = await OpenDirectoryMoveObjectsAsync(entries, intent, atTarget: false, token).ConfigureAwait(false);
                if (!objects.Objects[0].Snapshot.PlaceholderIdentity.Span.SequenceEqual(publicProof.ExpectedPlaceholderIdentity.Span))
                    throw new InvalidDataException("Root preparation requires the complete original public placeholder identity.");
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeProjectionInspection;
                foreach (var item in objects.Objects)
                    RequireDirectoryMemberProjection(await ReadProjectionAsync(item.Entry.MemberId, token).ConfigureAwait(false),
                        item.Entry, intent, atTarget: false, allowOriginal: false);
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeMemberReinspection;
                await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget: false, token).ConfigureAwait(false);
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeAnchorReinspection;
                var currentAnchor = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(anchor.Context.Member.MemberId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original sync-root anchor disappeared during preparation.");
                if (MirrorPulseRootLabelAnchorInspection.RequireProof(currentAnchor, intent) != anchorProof || _router.FindRegistration(root.RootId) != root)
                    throw new InvalidOperationException("Root preparation cannot replace its original anchor, role or current routing.");
                var after = await parent.InspectAsync(token).ConfigureAwait(false);
                MirrorPulseRootLabelAnchorInspection.RequireNative(after, await _fileSystem.Root.InspectAsync(token).ConfigureAwait(false), anchorProof, intent);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, after);
                subtreeStage = MirrorPulseRootLabelExecutionStage.SubtreeCatalogSeal;
                intent = intent with { PreparedAt = DateTimeOffset.UtcNow };
                prepared = await _catalog.PrepareNamespaceMemberMoveAsync(intent, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return prepared;
        }
        catch (Exception original) when (original is not StackOverflowException and not OutOfMemoryException)
        {
            if (MirrorPulseRootLabelExecutionFailureDiagnostics.Stage(original) == MirrorPulseRootLabelExecutionStage.Unknown)
                MirrorPulseRootLabelExecutionFailureDiagnostics.MarkStage(original, subtreeStage);
            throw;
        }
    }

    private async Task RequireOriginalRootSpellingAsync(string original, CancellationToken token)
    {
        int inspected = 0, found = 0;
        await foreach (var child in _fileSystem.Root.EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, token).ConfigureAwait(false))
        {
            if (++inspected > 4096) throw new InvalidOperationException("The local first-level namespace exceeds the bounded root inspection.");
            string path = child.RelativePath.Replace('\\', '/');
            if (!string.Equals(path, original, StringComparison.OrdinalIgnoreCase)) continue;
            if (path != original || child.Kind != CloudItemKind.Directory || ++found > 1)
                throw new InvalidDataException("The original root spelling cannot be inferred from a case alias or occupied name.");
        }
        if (found != 1) throw new InvalidDataException("The original root is absent before subtree preparation.");
    }
}
