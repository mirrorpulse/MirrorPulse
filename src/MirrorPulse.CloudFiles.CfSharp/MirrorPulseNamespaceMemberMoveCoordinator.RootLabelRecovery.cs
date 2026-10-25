using System.Security.Principal;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Settles only an already observed original root move, without native movement or role adoption.</summary>
    internal async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> RecoverObservedRootLabelAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original root Label operation is required.", nameof(operationId));
        return await _actor.RunAsync(token => RecoverObservedRootLabelAdmittedAsync(operationId, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveCompletion> RecoverObservedRootLabelAdmittedAsync(Guid operationId, CancellationToken token)
    {
        _actor.AssertAccess();
        var intent = await _catalog.ReadNamespaceMemberMoveAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original root Label subtree admission is missing.");
        if (intent.Version != 3 || intent.RemoteEntry is not null || _execution.OwnerSid.Value != intent.OwnerSid)
            throw new InvalidOperationException("Observed root Label recovery requires the original local admission and current owner.");
        var complete = await _catalog.ReadNamespaceDirectoryMoveCompletionAsync(operationId, token).ConfigureAwait(false);
        if (complete is not null)
        {
            _router.ReplaceRegistrations((await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots,
                await _catalog.ReadManagedRootNamesAsync(token).ConfigureAwait(false));
            return complete.Root;
        }
        intent = await _catalog.ValidateNamespaceMemberMoveExecutionAsync(operationId, token).ConfigureAwait(false);
        if (await _catalog.ReadNamespaceMemberMoveStartAsync(operationId, token).ConfigureAwait(false) is null)
            throw new InvalidOperationException("Observed root Label recovery requires its original before-native start.");
        var snapshot = await _catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Observed root Label recovery lost its original sealed subtree.");
        var history = await _catalog.ReadManagedRootRenameHistoryAsync(operationId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Observed root Label recovery lost its original Label history.");
        var registration = (await _catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).Roots.Single(root => root.RootId == intent.RootId);
        var proof = MirrorPulseManagedRootRenameCoordinator.Decode(history, registration);
        RequireDirectoryMoveProof(proof, intent);
        var childProof = await ReadProofAsync(intent.MemberId, intent.ProtectionId, intent, token).ConfigureAwait(false);
        var anchorProof = await ReadRootLabelAnchorAsync(intent, token).ConfigureAwait(false);
        var result = await MirrorPulseRootLabelTargetInspection.RunAsync(_catalog, _router, _actor, operationId, async (inspection, admittedToken) =>
        {
            MirrorPulseNamespaceMemberMoveCompletion settled = null!;
            // Reading exact old descriptors does not need, and cannot grant, the old role.
            // Startup must perform this original settlement before rotating any permissions.
            await _execution.RunNormalUserOperationAsync(async () =>
            {
                using WindowsIdentity caller = WindowsIdentity.GetCurrent();
                if (caller.User?.Value != intent.OwnerSid)
                    throw new UnauthorizedAccessException("Observed root Label recovery requires the original current-user owner.");
                await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(_fileSystem.Root, _router, admittedToken).ConfigureAwait(false);
                var before = await InspectRootLabelAnchorAsync(anchor, anchorProof, intent, admittedToken).ConfigureAwait(false);
                await RequireRootLabelAdmissionAsync(intent, childProof, anchorProof, admittedToken).ConfigureAwait(false);
                var reconciled = await _fileSystem.GetDirectory(intent.Source.RelativePath).ReconcileMoveAsync(proof, admittedToken).ConfigureAwait(false);
                RequireSuccessfulDirectoryReconciliation(reconciled);
                CancellationToken durable = CancellationToken.None;
                await RequireOriginalRootSpellingAsync(intent.Target.RelativePath, durable).ConfigureAwait(false);
                settled = await SettleRootLabelAsync(intent, snapshot, reconciled, proof, childProof, anchorProof, anchor, before, inspection, durable).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return settled;
        }, token).ConfigureAwait(false);
        _router.ReplaceRegistrations((await _catalog.ReadAdapterTopologyAsync(CancellationToken.None).ConfigureAwait(false)).Roots,
            await _catalog.ReadManagedRootNamesAsync(CancellationToken.None).ConfigureAwait(false));
        return result;
    }
}
