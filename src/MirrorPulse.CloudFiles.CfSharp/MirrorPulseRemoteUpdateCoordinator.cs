using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRemoteUpdateCheckpoint { AfterAdmission, AfterStart, AfterSdkApply, AfterSettlement }

/// <summary>Applies an existing-object upsert through its original SDK entry and durable product owner.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed partial class MirrorPulseRemoteUpdateCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteUpdateCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    internal async ValueTask<CloudRemoteApplyResult> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch, int ordinal,
        CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        var change = RequireChange(instance, batch, ordinal, options);
        var retained = await catalog.ReadNamespaceRemoteUpdateAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        Guid memberId;
        if (retained is null)
            memberId = (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
                .InspectAsync(change.RelativePath, change.ItemKind == CloudItemKind.Directory, token).ConfigureAwait(false)).MemberId;
        else { RequireOriginal(retained.Plan, instance, batch, ordinal, options); memberId = retained.Plan.Child.Context.Member.MemberId; }
        return await actor.RunAsync(async admitted =>
        {
            var child = retained?.Plan.Child ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(memberId, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote update has no protected registered object.");
            var parent = retained?.Plan.Parent ?? await catalog.ReadNamespaceMemberProtectionSnapshotAsync(
                child.Context.Location.ParentMemberId ?? throw new InvalidDataException("Remote updates cannot replace the namespace anchor."), admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote update lost its direct parent.");
            RequireRoute(instance, change.RelativePath, child, parent, requireCurrentRole: true);
            if (retained is null ? (await catalog.ReadNamespaceDispatchFencedRootsAsync(admitted).ConfigureAwait(false)).Contains(child.Context.Member.RootId!.Value) :
                !await catalog.CanResumeNamespaceRemoteUpdateAsync(retained.Plan, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents the original remote update.");
            CloudRemoteApplyResult result = null!;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                var item = Item(child); var parentItem = fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, admitted).ConfigureAwait(false);
                await using var childGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, admitted).ConfigureAwait(false);
                var parentBefore = await InspectAsync(parent, parentItem, parentGuard, admitted).ConfigureAwait(false);
                var before = await InspectAsync(child, item, childGuard, admitted).ConfigureAwait(false);
                var official = await ReadProjectionAsync(change.ItemId!.Value, child, admitted).ConfigureAwait(false);
                if (retained is null)
                {
                    var plan = new MirrorPulseNamespaceRemoteUpdatePlan(1, Guid.NewGuid(), instance, batch.BatchId,
                        Convert.ToHexString(batch.Fingerprint.Span), batch.Changes.Count, ordinal, change.ChangeId, ChangeFingerprint(change),
                        change.ItemId.Value, change.RemoteId, change.PreviousRemoteRevision, change.RemoteRevision, before.Revision,
                        official.RemoteRevision, before.Snapshot.IsPlaceholder, child, parent, before.Verification, parentBefore.Verification,
                        MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit,
                        Policy(options), DateTimeOffset.UtcNow);
                    retained = await catalog.PrepareNamespaceRemoteUpdateAsync(plan, admitted).ConfigureAwait(false);
                    if (checkpoint is not null) await checkpoint(MirrorPulseRemoteUpdateCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
                }
                if (retained.StartedAt is not null)
                    throw new InvalidOperationException("A started remote update requires its original SDK progress and fresh outcome recovery before another invocation.");
                if (before.Revision != retained.Plan.NativeRemoteRevision || official.RemoteRevision != retained.Plan.OfficialRemoteRevision ||
                    before.Snapshot.IsPlaceholder != retained.Plan.NativeIsPlaceholder)
                    throw new InvalidDataException("The prepared remote update lost its original native or official revision.");
                await RequireProgressAsync(retained.Plan, applied: false, admitted).ConfigureAwait(false);
                retained = await catalog.StartNamespaceRemoteUpdateAsync(retained.Plan.OperationId, DateTimeOffset.UtcNow, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteUpdateCheckpoint.AfterStart, admitted).ConfigureAwait(false);
                result = await fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteUpdateCheckpoint.AfterSdkApply, CancellationToken.None).ConfigureAwait(false);
                if (result.AppliedEntryCount <= ordinal) return;
                await SettleAsync(retained, conflicts, result, parentItem, parentGuard, parentBefore, item, childGuard,
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }, token).ConfigureAwait(false);
    }

    internal ValueTask SettleRecordedAsync(MirrorPulseNamespaceRemoteUpdate retained, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        RequireOriginal(retained.Plan, instance, batch, ordinal, options);
        return SettleRecordedAsync(retained, conflicts, token);
    }

    /// <summary>Read-only reconciliation before startup rotates any role. This never re-invokes the SDK.</summary>
    internal async ValueTask SettleRecordedAsync(MirrorPulseNamespaceRemoteUpdate original, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        await actor.RunAsync(async admitted =>
        {
            var plan = original.Plan;
            var retained = await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal, admitted).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote update owner disappeared.");
            if (retained.Plan != plan) throw new InvalidDataException("The retained SDK update entry changed during recovery.");
            if (retained.Outcome is not null) return true;
            if (!await catalog.CanResumeNamespaceRemoteUpdateAsync(plan, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents original SDK update settlement.");
            RequireRoute(plan.InstanceId, plan.Child.Context.Location.RelativePath, plan.Child, plan.Parent, requireCurrentRole: false);
            await RequireProgressAsync(plan, applied: true, admitted).ConfigureAwait(false);
            await execution.RunNormalUserOperationAsync(async () =>
            {
                var item = Item(plan.Child); var parent = fileSystem.GetDirectory(plan.Parent.Context.Location.RelativePath);
                await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, admitted).ConfigureAwait(false);
                await using var childGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, admitted).ConfigureAwait(false);
                var before = await InspectAsync(plan.Parent, parent, parentGuard, admitted).ConfigureAwait(false);
                await SettleAsync(retained, conflicts, null, parent, parentGuard, before, item, childGuard, admitted).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task SettleAsync(MirrorPulseNamespaceRemoteUpdate retained, MirrorPulseRemoteConflictProjector? conflicts,
        CloudRemoteApplyResult? result, CloudDirectory parent, MirrorPulseWindowsNamespacePermissionLease parentGuard,
        UpdateObservation parentBefore, CloudItem item, MirrorPulseWindowsNamespacePermissionLease childGuard, CancellationToken token)
    {
        var plan = retained.Plan;
        if (retained.StartedAt is not { } started) throw new InvalidDataException("Applied SDK update progress lost its original start.");
        await RequireProgressAsync(plan, applied: true, token).ConfigureAwait(false);
        var records = await fileSystem.ListRemoteConflictsAsync(token).ConfigureAwait(false);
        var matches = records.Where(record => record.Conflict.DetectedAt >= started &&
            record.Conflict.Change.ChangeId == plan.ChangeId && ChangeFingerprint(record.Conflict.Change) == plan.ChangeFingerprint).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The original SDK update has ambiguous durable conflicts.");
        if (result is not null)
        {
            var entry = result.Entries.SingleOrDefault(entry => entry.ChangeId == plan.ChangeId);
            if (result.AppliedEntryCount != plan.Ordinal + 1 || entry is null ||
                entry.Status is not (CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied or CloudRemoteApplyEntryStatus.Conflict) ||
                (entry.Status == CloudRemoteApplyEntryStatus.Conflict) != (matches.Length == 1) || result.ConflictIds.Count != matches.Length ||
                matches.Length == 1 && result.ConflictIds[0] != matches[0].ConflictId)
                throw new InvalidDataException("The SDK update return disagrees with its exact original progress or durable conflict.");
        }
        var childAfter = await InspectAsync(plan.Child, item, childGuard, token).ConfigureAwait(false);
        var official = await ReadProjectionAsync(plan.ItemId, plan.Child, token).ConfigureAwait(false);
        var parentAfter = await InspectAsync(plan.Parent, parent, parentGuard, token).ConfigureAwait(false);
        MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore.Object, parentAfter.Object);
        if (matches.Length == 1 && conflicts is not null)
            await conflicts.CaptureAsync(plan.InstanceId, matches[0].ConflictId, matches[0].Conflict, token).ConfigureAwait(false);
        var outcome = new MirrorPulseNamespaceRemoteUpdateOutcome(1, plan.OperationId,
            matches.Length == 0 ? MirrorPulseNamespaceRemoteUpdateDisposition.Applied : MirrorPulseNamespaceRemoteUpdateDisposition.Conflict,
            result is null ? MirrorPulseNamespaceRemoteUpdateEvidenceSource.RetainedSdkProgress : MirrorPulseNamespaceRemoteUpdateEvidenceSource.SdkReturn,
            matches.Length == 0 ? null : matches[0].ConflictId, matches.Length == 0 ? null : matches[0].Conflict.DetectedAt,
            childAfter.Revision, official.RemoteRevision, childAfter.Snapshot.IsPlaceholder,
            childAfter.Snapshot.SynchronizationState == CloudSynchronizationState.InSync,
            childAfter.Verification, parentAfter.Verification, DateTimeOffset.UtcNow);
        _ = await catalog.SettleNamespaceRemoteUpdateAsync(outcome, token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteUpdateCheckpoint.AfterSettlement, token).ConfigureAwait(false);
    }
}
