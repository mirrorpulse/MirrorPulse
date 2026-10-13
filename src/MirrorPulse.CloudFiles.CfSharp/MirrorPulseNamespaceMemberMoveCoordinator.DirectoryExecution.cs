using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    private async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> ExecuteDirectoryMoveAdmittedAsync(
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken token)
    {
        _actor.AssertAccess();
        var snapshot = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(intent.OperationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Directory execution requires the original sealed subtree.");
        var proof = CloudDirectoryMoveProof.Decode(Convert.FromBase64String(intent.DirectoryMoveEvidence
            ?? throw new InvalidDataException("Directory execution requires its original public evidence.")));
        RequireDirectoryMoveProof(proof, intent);
        var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
        var sourceProof = await ReadProofAsync(intent.Source.ParentMemberId, intent.Source.ParentProtectionId, intent, token).ConfigureAwait(false);
        var targetProof = await ReadProofAsync(intent.Target.ParentMemberId, intent.Target.ParentProtectionId, intent, token).ConfigureAwait(false);
        var start = await _catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token).ConfigureAwait(false);
        CloudDirectory source = _fileSystem.GetDirectory(intent.Source.RelativePath);
        CloudDirectory target = _fileSystem.GetDirectory(intent.Target.RelativePath);
        CloudDirectory sourceParent = source.Parent ?? throw new InvalidDataException("A controlled directory requires a source parent.");
        CloudDirectory targetParent = target.Parent ?? throw new InvalidDataException("A controlled directory requires a target parent.");
        MirrorPulseNamespaceMemberMoveCompletion result = null!;
        await _execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(intent);
            await using var sourceLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(sourceParent, _router, token).ConfigureAwait(false);
            await using var targetLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetParent, _router, token).ConfigureAwait(false);
            var sourceBefore = await InspectParentAsync(sourceParent, sourceLease, sourceProof, intent.Source, intent, token).ConfigureAwait(false);
            var targetBefore = await InspectParentAsync(targetParent, targetLease, targetProof, intent.Target, intent, token).ConfigureAwait(false);
            var sourceSnapshot = await source.InspectAsync(token).ConfigureAwait(false);
            bool atTarget = !sourceSnapshot.Exists;
            if (atTarget && start is null)
                throw new InvalidDataException("A missing source cannot create a before-native start retrospectively.");
            CloudDirectoryMoveReconciliationResult? publicResult = null;
            if (start is not null)
            {
                // Path existence cannot distinguish a case-only rename on Windows. The original
                // public proof authenticates whether the object actually moved before recovery.
                var recovered = await source.ReconcileMoveAsync(proof, token).ConfigureAwait(false);
                if (recovered.Outcome == CloudDirectoryMoveReconciliationOutcome.NotMoved &&
                    !recovered.NativeMoveObserved && !recovered.DurableProjectionCommitted)
                    atTarget = false;
                else
                {
                    RequireSuccessfulDirectoryReconciliation(recovered);
                    publicResult = recovered;
                    atTarget = true;
                }
            }
            CancellationToken operationToken = atTarget ? CancellationToken.None : token;
            if (!atTarget && !string.Equals(intent.Source.RelativePath, intent.Target.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                (await target.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The directory destination is occupied.");
            await using (var objects = await OpenDirectoryMoveObjectsAsync(snapshot.Entries, intent, atTarget, operationToken).ConfigureAwait(false))
            {
                if (!objects.Objects[0].Snapshot.PlaceholderIdentity.Span.SequenceEqual(proof.ExpectedPlaceholderIdentity.Span))
                    throw new InvalidDataException("The actual root identity no longer matches the original public move evidence.");
                foreach (var entry in snapshot.Entries)
                    RequireDirectoryMemberProjection(await ReadProjectionAsync(entry.MemberId, operationToken).ConfigureAwait(false), entry, intent,
                        atTarget, allowOriginal: atTarget);
                await ReinspectDirectoryMoveObjectsAsync(objects, intent, atTarget, operationToken).ConfigureAwait(false);
                await RequireUnchangedAdmissionAsync(intent, childProof, sourceProof, targetProof, operationToken).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceLease.InspectAsync(operationToken).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetLease.InspectAsync(operationToken).ConfigureAwait(false));
                if (start is null)
                    await _catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            }
            // Close every subtree metadata handle before CfSharp acquires its native move scope.
            // The external parent guards, role and actor remain held across the operation.
            if (publicResult is null)
            {
                var moved = await source.MoveToAsync(targetParent, TargetName(intent), new CloudMoveOptions(proof), token).ConfigureAwait(false);
                publicResult = moved.DirectoryReconciliation
                    ?? throw new InvalidDataException("CfSharp did not return directory reconciliation evidence.");
            }
            RequireSuccessfulDirectoryReconciliation(publicResult);
            CancellationToken durable = CancellationToken.None;
            await CheckpointAsync(MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove, durable).ConfigureAwait(false);
            result = await SettleDirectoryMoveAsync(intent, snapshot, publicResult, childProof, sourceProof, targetProof,
                sourceLease, targetLease, sourceParent, targetParent, sourceBefore, targetBefore, durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }

    private static void RequireSuccessfulDirectoryReconciliation(CloudDirectoryMoveReconciliationResult result)
    {
        if (result.Outcome is not (CloudDirectoryMoveReconciliationOutcome.Projected or CloudDirectoryMoveReconciliationOutcome.AlreadyProjected) ||
            !result.NativeMoveObserved || !result.DurableProjectionCommitted || result.RequiresFullRescan)
            throw new InvalidOperationException($"Public directory recovery remains incomplete: {result.Outcome}, stage {result.Stage}, native HRESULT {result.NativeHResult}.", result.Error);
    }
}
