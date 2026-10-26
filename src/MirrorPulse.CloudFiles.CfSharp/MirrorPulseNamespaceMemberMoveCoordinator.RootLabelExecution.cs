using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    // The Host root command must retain instance/root/library quiescence.
    internal async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteRootLabelAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original root Label operation is required.", nameof(operationId));
        return await _actor.RunAsync(token => ExecuteRootLabelAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteRootLabelAdmittedAsync(Guid operationId, CancellationToken token)
    {
        _actor.AssertAccess();
        var intent = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original root Label subtree admission is missing.");
        if (intent.Version != 3 || intent.RemoteEntry is not null)
            throw new InvalidOperationException("Root Label execution requires its original local sealed admission.");
        var complete = await _catalog.ReadNamespaceDirectoryMoveCompletionAsync(operationId, token).ConfigureAwait(false);
        if (complete is not null)
        {
            // A prior return may have been lost after atomic settlement but before router refresh.
            // Refresh only today's catalog, never the historical operation's old endpoints.
            _router.ReplaceRegistrations((await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots,
                await _catalog.ReadManagedRootNamesAsync(token).ConfigureAwait(false));
            return complete.Root;
        }
        intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
        var snapshot = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original root Label subtree is missing.");
        var history = await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original root Label history is missing.");
        var registration = (await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(root => root.RootId == intent.RootId);
        var proof = MirrorPulseManagedRootRenameCoordinator.Decode(history, registration);
        RequireDirectoryMoveProof(proof, intent);
        var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
        var anchorProof = await ReadRootLabelAnchorAsync(intent, token).ConfigureAwait(false);
        var start = await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false);
        CloudDirectory source = _fileSystem.GetDirectory(intent.Source.RelativePath);
        var result = await MirrorPulseRootLabelTargetInspection.RunAsync(_catalog, _router, _actor, operationId, async (inspection, admittedToken) =>
        {
            MirrorPulseNamespaceMemberMoveCompletion settled = null!;
            await _execution.RunNamespaceOperationAsync(async () =>
            {
                RequireCaller(intent);
                await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(_fileSystem.Root, _router, admittedToken).ConfigureAwait(false);
                var anchorBefore = await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, admittedToken).ConfigureAwait(false);
                var publicResult = await source.ReconcileMoveAsync(proof, admittedToken).ConfigureAwait(false);
                bool atTarget = publicResult.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved;
                if (atTarget)
                {
                    if (start is null) throw new InvalidDataException("A moved root cannot invent an original before-native start.");
                    RequireSuccessfulDirectoryReconciliation(publicResult);
                }
                else if (publicResult.NativeMoveObserved || publicResult.DurableProjectionCommitted || publicResult.RequiresFullRescan ||
                    history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared)
                    throw new InvalidOperationException("Root Label execution cannot replace inconsistent original SDK or native progress.");
                CancellationToken operationToken = atTarget ? CancellationToken.None : admittedToken;
                await RequireOriginalRootSpellingAsync(atTarget ? intent.Target.RelativePath : intent.Source.RelativePath, operationToken).ConfigureAwait(false);
                if (!atTarget && !string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    (await _fileSystem.GetDirectory(intent.Target.RelativePath).InspectAsync(operationToken).ConfigureAwait(false)).Exists)
                    throw new IOException("The root Label destination is occupied.");
                await using (var objects = await OpenRootLabelObjectsAsync(snapshot.Entries, intent, atTarget, inspection, operationToken).ConfigureAwait(false))
                {
                    if (!objects.Objects[0].Snapshot.PlaceholderIdentity.Span.SequenceEqual(proof.ExpectedPlaceholderIdentity.Span))
                        throw new InvalidDataException("Root Label execution lost its complete original public identity.");
                    foreach (var entry in snapshot.Entries)
                        RequireDirectoryMemberProjection(await ReadProjectionAsync(entry.MemberId, operationToken).ConfigureAwait(false), entry, intent,
                            atTarget, allowOriginal: atTarget);
                    await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget, operationToken).ConfigureAwait(false);
                    await RequireRootLabelAdmissionAsync(intent, childProof, anchorProof, operationToken).ConfigureAwait(false);
                    MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(anchorBefore,
                        await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, operationToken).ConfigureAwait(false));
                    if (start is null)
                        await _catalog.RecordNamespaceMemberMoveStartAsync(new(1, operationId, DateTimeOffset.UtcNow), operationToken).ConfigureAwait(false);
                }
                if (!atTarget)
                {
                    // Release subtree handles; retain the physical anchor and the original role/actor.
                    var moved = await source.MoveToAsync(_fileSystem.Root, TargetName(intent), new CloudMoveOptions(proof), operationToken).ConfigureAwait(false);
                    publicResult = moved.DirectoryReconciliation ?? throw new InvalidDataException("Root Label movement returned no public reconciliation.");
                }
                RequireSuccessfulDirectoryReconciliation(publicResult);
                CancellationToken durable = CancellationToken.None;
                await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove, durable).ConfigureAwait(false);
                await RequireOriginalRootSpellingAsync(intent.Target.RelativePath, durable).ConfigureAwait(false);
                settled = await SettleRootLabelAsync(intent, snapshot, publicResult, proof, childProof, anchorProof, anchor, anchorBefore, inspection, durable).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return settled;
        }, token).ConfigureAwait(false);
        // No target routing exists before the complete original product transaction returns.
        _router.ReplaceRegistrations((await _catalog.ReadAdapterTopologyAsync(CancellationToken.None).ConfigureAwait(false)).Roots,
            await _catalog.ReadManagedRootNamesAsync(CancellationToken.None).ConfigureAwait(false));
        return result;
    }
}
