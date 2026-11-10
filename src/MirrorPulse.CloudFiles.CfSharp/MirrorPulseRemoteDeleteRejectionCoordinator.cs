using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRemoteDeleteRejectionCheckpoint { AfterAdmission, AfterSdkApply, AfterSettlement }

/// <summary>Retains the real SDK rejection of an absent identity without inventing a native member retirement.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteDeleteRejectionCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor, Func<MirrorPulseRemoteDeleteRejectionCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    internal async ValueTask<CloudRemoteApplyResult?> TryApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        int ordinal, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token,
        bool metadataRecovery = false)
    {
        var change = MirrorPulseRemoteRetirementBatchCoordinator.RequireChange(instance, batch, ordinal, options);
        var original = await catalog.ReadRemoteDeleteRejectionAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        if (metadataRecovery && original is null) throw new InvalidDataException("Metadata recovery requires its retained original preserving Delete attempt.");
        if (original is null && await ReadItemAsync(change.ItemId!.Value, token).ConfigureAwait(false) is not null) return null;
        RootRegistration root;
        Guid rootMember;
        if (original is null)
        {
            var route = router.ResolveCurrentPath(change.RelativePath); root = router.GetRegistration(route.InstanceId, route.RootKey);
            RequireCurrentRoot(root, instance, change);
            rootMember = (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
                .InspectAsync(root.DirectoryName, true, token).ConfigureAwait(false)).MemberId;
        }
        else
        {
            RequireOriginal(original.Admission, instance, batch, ordinal, options);
            root = router.FindRegistration(original.Admission.RootId) ?? throw new InvalidDataException("The original preserving Delete lost its configured root.");
            RequireCurrentRoot(root, instance, change, metadataRecovery); rootMember = original.Admission.RootMemberId;
        }
        return await actor.RunAsync<CloudRemoteApplyResult?>(async admitted =>
        {
            // Recheck the actual official absence under the single namespace owner.
            // A surviving row with a missing native path retains the old strict gap refusal.
            if (await ReadItemAsync(change.ItemId!.Value, admitted).ConfigureAwait(false) is not null)
            {
                if (original is not null) throw new InvalidDataException("The original missing-identity attempt regained an unproved SDK projection.");
                return null;
            }
            await RequirePrefixAsync(batch, ordinal, applied: false, admitted).ConfigureAwait(false);
            if (original is null)
            {
                var admission = new MirrorPulseRemoteDeleteRejectionAdmission(1, Guid.NewGuid(), instance, root.RootId,
                    root.UniquenessKey, root.DirectoryName, rootMember, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span),
                    batch.Changes.Count, ordinal, EntryFingerprint(batch, ordinal), change.ChangeId, change.ItemId!.Value,
                    change.RelativePath.Replace('\\', '/'), change.ItemKind == CloudItemKind.Directory,
                    MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit,
                    Policy(options), DateTimeOffset.UtcNow);
                original = await catalog.PrepareRemoteDeleteRejectionAsync(admission, admitted).ConfigureAwait(false);
                if (checkpoint is not null) await checkpoint(MirrorPulseRemoteDeleteRejectionCheckpoint.AfterAdmission, admitted).ConfigureAwait(false);
            }
            if (original.Outcome is not null || !await catalog.CanResumeRemoteDeleteRejectionAsync(original.Admission, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("The original preserving Delete cannot overtake another namespace owner or its observed result.");
            var applied = await execution.RunNormalUserOperationAsync(async () =>
                await fileSystem.ApplyRemoteChangesAsync(batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false)).ConfigureAwait(false);
            if (checkpoint is not null) await checkpoint(MirrorPulseRemoteDeleteRejectionCheckpoint.AfterSdkApply, CancellationToken.None).ConfigureAwait(false);
            if (applied.AppliedEntryCount <= ordinal) return applied;
            var entry = applied.Entries.SingleOrDefault(value => value.ChangeId == change.ChangeId);
            if (applied.AppliedEntryCount != ordinal + 1 || entry?.Status != CloudRemoteApplyEntryStatus.Conflict ||
                entry.Conflict?.Reason != CloudRemoteConflictReason.MissingItem || applied.ConflictIds.Count != 1 ||
                !MirrorPulseRemoteRetirementBatchCoordinator.Matches(batch, ordinal, entry.Conflict.Change))
                throw new InvalidDataException("The preserving missing-identity attempt did not return its actual original SDK rejection.");
            await SettleUnderActorAsync(original, instance, batch, ordinal, options, applied.ConflictIds[0], conflicts, CancellationToken.None).ConfigureAwait(false);
            return applied;
        }, token).ConfigureAwait(false);
    }

    /// <summary>Completes only the already received preserving rejection before a new role's namespace audit.</summary>
    /// <remarks>This invokes no Worker, local journal ACK, native mutation or product cursor publication.
    /// Disabled roots retain their original incoming header and every local SDK operation.</remarks>
    internal async ValueTask RecoverBeforeAuditAsync(RootRegistration root, MirrorPulseRemoteDeleteRejection original,
        CloudRemoteChangeBatch batch, CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) || original.Admission.RootId != root.RootId ||
            original.Admission.InstanceId != root.InstanceId || original.Admission.RootKey != root.UniquenessKey ||
            original.Admission.RootDirectoryName != root.DirectoryName || original.Outcome is not null)
            throw new InvalidDataException("Metadata recovery requires the exact configured root and unsettled original rejection.");
        RequireOriginal(original.Admission, root.InstanceId, batch, original.Admission.Ordinal, options);
        int count;
        await using (var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var progress = await read.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
            if (progress is not null && (progress.TotalEntryCount != batch.Changes.Count || !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span)))
                throw new InvalidDataException("The received preserving Delete lost its original SDK batch before startup.");
            count = progress?.AppliedEntryCount ?? 0;
            await read.RollbackAsync(token).ConfigureAwait(false);
        }
        if (count == original.Admission.Ordinal + 1)
            await SettleRecordedAsync(original, root.InstanceId, batch, original.Admission.Ordinal, options, conflicts, token).ConfigureAwait(false);
        else if (count == original.Admission.Ordinal)
        {
            _ = await TryApplyAsync(root.InstanceId, batch, original.Admission.Ordinal, options, conflicts, token,
                metadataRecovery: true).ConfigureAwait(false);
        }
        else throw new InvalidDataException("Another SDK entry overtook the unsettled received preserving Delete.");
        if ((await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, batch.BatchId, original.Admission.Ordinal, token).ConfigureAwait(false))?.Outcome is null)
            throw new IOException("The received preserving Delete still lacks its actual durable SDK rejection.");
    }

    internal static CloudRemoteApplyOptions OriginalOptions(MirrorPulseRemoteDeleteRejectionAdmission original) => new()
    {
        MaximumEntries = 1,
        PreserveUnsynchronizedLocalContent = original.Policy.PreserveUnsynchronizedLocalContent,
        SuppressLocalEcho = original.Policy.SuppressLocalEcho,
        EchoSuppressionLifetime = TimeSpan.FromTicks(original.Policy.EchoSuppressionLifetimeTicks),
        EchoSuppressionObservationCount = original.Policy.EchoSuppressionObservationCount
    };

    internal async ValueTask SettleRecordedAsync(MirrorPulseRemoteDeleteRejection original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options,
        MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        RequireOriginal(original.Admission, instance, batch, ordinal, options);
        await actor.RunAsync(async admitted =>
        {
            if (original.Outcome is not null) return true;
            if (!await catalog.CanResumeRemoteDeleteRejectionAsync(original.Admission, admitted).ConfigureAwait(false))
                throw new InvalidOperationException("Another namespace owner prevents the original SDK rejection settlement.");
            await SettleUnderActorAsync(original, instance, batch, ordinal, options, null, conflicts, admitted).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task SettleUnderActorAsync(MirrorPulseRemoteDeleteRejection original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options, Guid? observedConflict,
        MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        actor.AssertAccess(); RequireOriginal(original.Admission, instance, batch, ordinal, options);
        await RequirePrefixAsync(batch, ordinal, applied: true, token).ConfigureAwait(false);
        CloudRemoteConflictRecord record;
        if (observedConflict is { } id)
            record = await execution.RunNormalUserOperationAsync(async () =>
                await fileSystem.GetRemoteConflictAsync(id, token).ConfigureAwait(false)).ConfigureAwait(false)
                ?? throw new InvalidDataException("The actual original SDK rejection was not durable before product settlement.");
        else
        {
            // The public SDK currently lists decoded conflicts without an indexed
            // original-entry query, as in existing retirement outcome recovery.
            // Never infer its original rejection from an AlreadyApplied prefix alone.
            var records = await execution.RunNormalUserOperationAsync(async () =>
                await fileSystem.ListRemoteConflictsAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            var matching = records.Where(value => value.Conflict.Reason == CloudRemoteConflictReason.MissingItem &&
                value.Conflict.DetectedAt >= original.Admission.StartedAt &&
                MirrorPulseRemoteRetirementBatchCoordinator.Matches(batch, ordinal, value.Conflict.Change)).ToArray();
            if (matching.Length != 1) throw new InvalidDataException("The original preserving Delete has no unique actual durable SDK rejection.");
            record = matching[0];
        }
        if (record.Conflict.Reason != CloudRemoteConflictReason.MissingItem || record.Conflict.DetectedAt < original.Admission.StartedAt ||
            !MirrorPulseRemoteRetirementBatchCoordinator.Matches(batch, ordinal, record.Conflict.Change))
            throw new InvalidDataException("An unrelated SDK conflict cannot settle the original preserving Delete.");
        _ = await catalog.CompleteRemoteDeleteRejectionAsync(original.Admission,
            new(original.Admission.OperationId, record.ConflictId, record.Conflict.DetectedAt, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(MirrorPulseRemoteDeleteRejectionCheckpoint.AfterSettlement, token).ConfigureAwait(false);
        if (conflicts is not null) await conflicts.CaptureAsync(instance, record.ConflictId, record.Conflict, token).ConfigureAwait(false);
    }

    internal static void RequireOriginal(MirrorPulseRemoteDeleteRejectionAdmission original, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyOptions options)
    {
        var change = MirrorPulseRemoteRetirementBatchCoordinator.RequireChange(instance, batch, ordinal, options);
        if (original.InstanceId != instance || original.BatchId != batch.BatchId || original.BatchEntryCount != batch.Changes.Count ||
            original.BatchFingerprint != Convert.ToHexString(batch.Fingerprint.Span) || original.Ordinal != ordinal ||
            original.EntryFingerprint != EntryFingerprint(batch, ordinal) || original.ChangeId != change.ChangeId ||
            original.ItemId != change.ItemId || original.RelativePath != change.RelativePath.Replace('\\', '/') ||
            original.IsDirectory != (change.ItemKind == CloudItemKind.Directory) || original.Policy != Policy(options) ||
            !MirrorPulseRemoteLibraryProvenance.SupportsPreservingDeleteRejection(original.PackageVersion, original.PackageSource))
            throw new InvalidDataException("The preserving Delete changed its immutable original batch, entry, publication or policy.");
    }

    private static void RequireCurrentRoot(RootRegistration root, InstanceId instance, CloudRemoteChange change, bool metadataRecovery = false)
    {
        if (root.State != RootRegistrationState.Active && !(metadataRecovery && root.State == RootRegistrationState.Disabled) || root.InstanceId != instance ||
            !change.RelativePath.Replace('\\', '/').StartsWith(root.DirectoryName + "/", StringComparison.Ordinal) ||
            !MirrorPulsePlaceholderIdentity.BelongsToRoot(root, new(change.ItemId!.Value, change.RemoteId, change.RemoteRevision)))
            throw new InvalidDataException("A preserving Delete requires its actual active root and owned original public identity.");
    }

    private static string EntryFingerprint(CloudRemoteChangeBatch batch, int ordinal) => Convert.ToHexString(
        new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, [batch.Changes[ordinal]], batch.FinalCursor).Fingerprint.Span);

    private static MirrorPulseRemoteBirthPolicy Policy(CloudRemoteApplyOptions options) => new(options.PreserveUnsynchronizedLocalContent,
        options.SuppressLocalEcho, options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount);

    private async Task<CloudItemState?> ReadItemAsync(Guid item, CancellationToken token)
    {
        await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var result = await read.Items.GetByItemIdAsync(item, token).ConfigureAwait(false);
        await read.RollbackAsync(token).ConfigureAwait(false); return result;
    }

    private async Task RequirePrefixAsync(CloudRemoteChangeBatch batch, int ordinal, bool applied, CancellationToken token)
    {
        await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await read.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false); int count = ordinal + (applied ? 1 : 0);
        if (progress is null ? count != 0 : progress.AppliedEntryCount != count || progress.TotalEntryCount != batch.Changes.Count ||
            !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) || progress.LastAppliedChangeId != (count == 0 ? null : batch.Changes[count - 1].ChangeId) ||
            !progress.Cursor.Span.SequenceEqual(MirrorPulseRemoteBirthBatchCoordinator.PrefixCursor(batch, count).Span))
            throw new InvalidDataException("The preserving Delete lost its exact original SDK prefix.");
        await read.RollbackAsync(token).ConfigureAwait(false);
    }
}
