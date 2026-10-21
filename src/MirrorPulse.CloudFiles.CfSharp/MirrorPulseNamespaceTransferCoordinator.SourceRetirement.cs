using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseTransferSourceRetirementCheckpoint { BeforeNativeDeletion, AfterNativeDeletion, AfterNativeReceipt }

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal Func<MirrorPulseTransferSourceRetirementCheckpoint, CancellationToken, ValueTask>? SourceRetirementCheckpoint { get; init; }

    /// <summary>Retires only the guarded original source after its owned conditional remote delete and current whole-target proof.</summary>
    /// <remarks>Private original bytes and both root fences remain. This does not acknowledge the original journal.</remarks>
    public async ValueTask<MirrorPulseNamespaceRetirement> RetireSourceAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        var retained = await catalog.ReadNamespaceRetirementAsync(copy.Plan.DeleteOperationId, token).ConfigureAwait(false);
        if (retained?.Phase == MirrorPulseNamespaceRetirementPhase.Completed) return retained;
        _ = await ExecuteSourceDeleteAsync(operationId, workers, retireLocal: true, token).ConfigureAwait(false);
        return await catalog.ReadNamespaceRetirementAsync(copy.Plan.DeleteOperationId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original transfer source retirement receipt disappeared.");
    }

    private async Task RetireGuardedSourceAsync(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement? retained,
        CloudFile file, MirrorPulseWindowsNamespacePermissionLease childMetadata, MirrorPulseNamespaceMemberProtectionSnapshot target,
        CloudFile targetFile, MirrorPulseWindowsNamespacePermissionLease targetGuard, MirrorPulseTransferSourceDataLease data,
        MirrorPulseTransferTargetVerifier verifier, CancellationToken token)
    {
        actor.AssertAccess();
        var proof = await execution.RunNormalUserOperationAsync(() => verifier.ReadCurrentAsync(copy, token)).ConfigureAwait(false);
        await RequireCurrentTargetAsync(copy, target, targetFile, targetGuard, token).ConfigureAwait(false);
        retained ??= await catalog.PrepareNamespaceTransferSourceRetirementAsync(copy.Plan.OperationId, proof,
            target.Context.Protection.ProtectionId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        if (retained.Intent.TransferEntry?.OperationId != copy.Plan.OperationId ||
            retained.Intent.TransferEntry.TargetProtectionId != target.Context.Protection.ProtectionId ||
            retained.Phase != MirrorPulseNamespaceRetirementPhase.Prepared)
            throw new InvalidDataException("The current source retirement no longer owns its original target and pending source.");
        _ = await ReadSourceProjectionAsync(file, copy.Plan.Source, token).ConfigureAwait(false);
        await data.VerifyAsync(copy.Plan.Length, copy.Plan.Sha256, token).ConfigureAwait(false);
        retained = await catalog.StartNamespaceTransferSourceRetirementAsync(copy.Plan.OperationId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion, token).ConfigureAwait(false);
        // Only this child metadata lease denies delete sharing. Both parent chains and the SAME
        // write-excluding data object remain held across the public SDK deletion and durable receipt.
        await childMetadata.DisposeAsync().ConfigureAwait(false);
        await file.DeleteAsync(token).ConfigureAwait(false);
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion, CancellationToken.None).ConfigureAwait(false);
        await data.VerifyAsync(copy.Plan.Length, copy.Plan.Sha256, CancellationToken.None).ConfigureAwait(false);
        var receipt = await ReadOriginalSourceRetirementReceiptAsync(copy, retained, CancellationToken.None).ConfigureAwait(false);
        await RequireCurrentTargetAsync(copy, target, targetFile, targetGuard, CancellationToken.None).ConfigureAwait(false);
        _ = await catalog.RecordNamespaceTransferSourceRetiredAsync(copy.Plan.OperationId, receipt, CancellationToken.None).ConfigureAwait(false);
        if (SourceRetirementCheckpoint is not null)
            await SourceRetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeReceipt, CancellationToken.None).ConfigureAwait(false);
        _ = await catalog.CompleteNamespaceTransferSourceRetirementAsync(copy.Plan.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RecoverRetiredSourceUnderActorAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceRetirement retained, MirrorPulseTransferWorkerServices workers, CancellationToken token)
    {
        actor.AssertAccess();
        await execution.RunNamespaceOperationAsync(async () =>
        {
            foreach (var member in new[] { copy.Plan.SourceParent, copy.Plan.TargetParent })
            {
                RequireLiveRoute(member);
                if (member.Context.Protection.RoleSid != execution.RoleSid.Value || member.Context.Member.OwnerSid != execution.OwnerSid.Value ||
                    await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.Context.Member.MemberId, token).ConfigureAwait(false) != member)
                    throw new InvalidOperationException("Unfinished source retirement recovery requires current-role original parents.");
            }
            await using var sourceParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(copy.Plan.SourceParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            await using var targetParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                fileSystem.GetDirectory(copy.Plan.TargetParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
            var sourceBefore = await sourceParent.InspectAsync(token).ConfigureAwait(false);
            var targetBefore = await targetParent.InspectAsync(token).ConfigureAwait(false);
            RequireNative(sourceBefore, copy.Plan.SourceParent); RequireNative(targetBefore, copy.Plan.TargetParent);
            var target = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(copy.TargetPublication!.MemberId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original published transfer target disappeared during source retirement recovery.");
            if (target.Context.Protection.ProtectionId != retained.Intent.TransferEntry!.TargetProtectionId)
                throw new InvalidDataException("Recovery cannot borrow a different target protection epoch.");
            var targetFile = fileSystem.GetFile(copy.Plan.TargetRelativePath);
            await using var targetGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetFile, router, token).ConfigureAwait(false);
            await RequireCurrentTargetAsync(copy, target, targetFile, targetGuard, token).ConfigureAwait(false);
            await using var input = await execution.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(copy.Plan, token)).ConfigureAwait(false);
            _ = await execution.RunNormalUserOperationAsync(() => new MirrorPulseTransferTargetVerifier(catalog, workers.Stats,
                workers.Ranges, workers.Directories).ReadCurrentAsync(copy, token)).ConfigureAwait(false);
            // Missing path alone cannot establish which native object was removed. Only the
            // retained before-native item and its actual public tombstone can settle this receipt.
            var fresh = await ReadOriginalSourceRetirementReceiptAsync(copy, retained, token).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore, await sourceParent.InspectAsync(token).ConfigureAwait(false));
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore, await targetParent.InspectAsync(token).ConfigureAwait(false));
            if (retained.TransferReceipt is null)
                _ = await catalog.RecordNamespaceTransferSourceRetiredAsync(copy.Plan.OperationId, fresh, CancellationToken.None).ConfigureAwait(false);
            _ = await catalog.CompleteNamespaceTransferSourceRetirementAsync(copy.Plan.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceTransferRetirementReceipt> ReadOriginalSourceRetirementReceiptAsync(
        MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement retained, CancellationToken token)
    {
        var native = await fileSystem.GetFile(copy.Plan.Source.Context.Location.RelativePath).InspectAsync(token).ConfigureAwait(false);
        if (retained.StartedAt is null || retained.StartedItemId is not { } originalItem || native.Exists || !native.IsTombstone || native.ItemId != originalItem)
            throw new InvalidDataException("The original started source deletion has no owned public SDK tombstone; preserve private bytes and fences for explicit recovery.");
        await using var parent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
            fileSystem.GetDirectory(copy.Plan.SourceParent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
        var facts = await parent.InspectAsync(token).ConfigureAwait(false); RequireNative(facts, copy.Plan.SourceParent);
        return new(1, copy.Plan.OperationId, copy.Plan.DeleteOperationId, originalItem, copy.Plan.Source.Context.Member.LocalObject,
            true, true, new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt), DateTimeOffset.UtcNow);
    }
}
