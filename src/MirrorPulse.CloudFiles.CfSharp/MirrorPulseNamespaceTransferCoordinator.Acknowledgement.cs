using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseTransferAcknowledgementCheckpoint { BeforeSdkAcknowledgement, AfterSdkAcknowledgement }

public sealed partial class MirrorPulseNamespaceTransferCoordinator
{
    internal IMirrorPulseRetiredTransferObservationHandler? RetiredSourceObservations { get; init; }
    internal Func<MirrorPulseTransferAcknowledgementCheckpoint, CancellationToken, ValueTask>? AcknowledgementCheckpoint { get; init; }

    /// <summary>Settles the immutable original claim through the Host's running public feed.</summary>
    /// <remarks>The caller retains both root admissions and ordered instance gates. A retry reuses
    /// its first durable invocation, including after the SDK committed but the Host receipt was lost.</remarks>
    public async ValueTask<MirrorPulseNamespaceTransferCopy> AcknowledgeAsync(Guid operationId,
        MirrorPulseTransferWorkerServices workers, CancellationToken token)
    {
        var copy = await ReadAsync(operationId, token).ConfigureAwait(false);
        if (copy.Acknowledgement?.AcknowledgedAt is not null) return copy;
        var journal = JournalFeed ?? throw new InvalidOperationException("Transfer completion requires the Host's running original journal.");
        if (RetiredSourceObservations is null || copy.Plan.Version != 2 || copy.Plan.CommandOrigin is null || copy.JournalClaim is null)
            throw new InvalidOperationException("Transfer completion requires the original command, captured claim and installed late-observation protection.");
        return await actor.RunAsync(async stop =>
        {
            var current = await ReadAsync(operationId, stop).ConfigureAwait(false);
            if (current.Acknowledgement?.AcknowledgedAt is not null) return current;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                foreach (var parent in new[] { current.Plan.SourceParent, current.Plan.TargetParent })
                {
                    RequireLiveRoute(parent);
                    if (parent.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value ||
                        await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent.Context.Member.MemberId, stop).ConfigureAwait(false) != parent)
                        throw new InvalidOperationException("Unfinished transfer acknowledgement requires its current original parents and execution role.");
                }
                if (protocolVersion(current.Plan.SourceRoute.InstanceId) != 2 ||
                    protocolVersion(current.Plan.TargetRoute.InstanceId) != current.Plan.TargetRoute.ProtocolVersion)
                    throw new InvalidOperationException("The original transfer protocols changed before acknowledgement.");
                var retirement = await catalog.ReadNamespaceRetirementAsync(current.Plan.DeleteOperationId, stop).ConfigureAwait(false);
                if (retirement is not
                    {
                        Phase: MirrorPulseNamespaceRetirementPhase.Completed, TransferReceipt: not null,
                        TransferSourceProjection: not null, TransferSourceNameEpoch: not null
                    })
                    throw new InvalidOperationException("Original acknowledgement requires its completed source generation and settled name witness.");
                await using var sourceParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                    fileSystem.GetDirectory(current.Plan.SourceParent.Context.Location.RelativePath), router, stop).ConfigureAwait(false);
                await using var targetParent = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
                    fileSystem.GetDirectory(current.Plan.TargetParent.Context.Location.RelativePath), router, stop).ConfigureAwait(false);
                var sourceBefore = await sourceParent.InspectAsync(stop).ConfigureAwait(false);
                var targetBefore = await targetParent.InspectAsync(stop).ConfigureAwait(false);
                RequireNative(sourceBefore, current.Plan.SourceParent); RequireNative(targetBefore, current.Plan.TargetParent);
                _ = await ReadOriginalSourceRetirementReceiptAsync(current, retirement, stop).ConfigureAwait(false);
                var target = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(current.TargetPublication!.MemberId, stop).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original transfer target lost its current membership.");
                var targetFile = fileSystem.GetFile(current.Plan.TargetRelativePath);
                await using var targetGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(targetFile, router, stop).ConfigureAwait(false);
                RequireNative(await targetGuard.InspectAsync(stop).ConfigureAwait(false), target);
                RequireBinding(await targetFile.InspectAsync(stop).ConfigureAwait(false), target, requireResident: false);
                await using var input = await execution.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(current.Plan, stop)).ConfigureAwait(false);
                if (current.Acknowledgement is null)
                {
                    await RequireCurrentTargetAsync(current, target, targetFile, targetGuard, stop).ConfigureAwait(false);
                    var proof = await execution.RunNormalUserOperationAsync(() => new MirrorPulseTransferTargetVerifier(catalog,
                        workers.Stats, workers.Ranges, workers.Directories).ReadCurrentAsync(current, stop)).ConfigureAwait(false);
                    current = await catalog.StartNamespaceTransferAcknowledgementAsync(operationId, proof,
                        target.Context.Protection.ProtectionId, DateTimeOffset.UtcNow, stop).ConfigureAwait(false);
                }
                // A prior accepted invocation owns only these GUIDs. Replaying it must not demand
                // unchanged historical target contents or replace later user edits with old bytes.
                var originalStart = current.Acknowledgement!;
                if (AcknowledgementCheckpoint is not null)
                    await AcknowledgementCheckpoint(MirrorPulseTransferAcknowledgementCheckpoint.BeforeSdkAcknowledgement, stop).ConfigureAwait(false);
                if (originalStart.Kind == MirrorPulseTransferAcknowledgementKind.OriginalJournal)
                    await execution.RunNormalUserOperationAsync(async () => await journal.AcknowledgeAsync(current.JournalClaim!.Members
                        .Select(member => new CloudLocalChangeAcknowledgement(member.OperationId, null)).ToArray(), stop).ConfigureAwait(false)).ConfigureAwait(false);
                if (AcknowledgementCheckpoint is not null)
                    await AcknowledgementCheckpoint(MirrorPulseTransferAcknowledgementCheckpoint.AfterSdkAcknowledgement, CancellationToken.None).ConfigureAwait(false);
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(sourceBefore,
                    await sourceParent.InspectAsync(CancellationToken.None).ConfigureAwait(false));
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(targetBefore,
                    await targetParent.InspectAsync(CancellationToken.None).ConfigureAwait(false));
                current = await catalog.CompleteNamespaceTransferAcknowledgementAsync(originalStart,
                    DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return current;
        }, token).ConfigureAwait(false);
    }
}
