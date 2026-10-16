using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseRemoteNamespaceBatchCheckpoint { AfterMoveStart, AfterSdkApply }

/// <summary>Admits and settles one original SDK entry before any Adapter cursor can be published.</summary>
/// <remarks>The current managed path supports moves. Other entry kinds remain closed until
/// their birth, replacement and retirement lifecycles are connected. An uncertain native start
/// requires fresh original outcome recovery and never grants blind redispatch.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRemoteNamespaceBatchDriver(
    CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    Func<MirrorPulseRemoteNamespaceBatchCheckpoint, CancellationToken, ValueTask>? checkpoint = null)
{
    private readonly MirrorPulseNamespaceMemberMoveCoordinator _moves = new(fileSystem, state, catalog, router, execution, actor);
    private readonly MirrorPulseNamespaceMemberInspector _members = new(fileSystem, catalog, router, execution, actor);

    internal async ValueTask<CloudRemoteApplyResult> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions options, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        // Validate before retaining a product operation, including options the SDK would reject.
        if (options.MaximumEntries is < 1 or > 65_536 || options.EchoSuppressionObservationCount is < 1 or > 64 ||
            options.EchoSuppressionLifetime <= TimeSpan.Zero || options.EchoSuppressionLifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ConflictResolver is not null)
            throw new NotSupportedException("Managed remote entries require durable conflicts before an explicit product decision.");
        if (batch.Changes.Count > 65_536) throw new InvalidDataException("The managed remote batch exceeds its entry bound.");
        if (batch.Changes.Any(change => change.Kind != CloudRemoteChangeKind.Move))
            throw new NotSupportedException("This managed remote batch requires a birth, replacement or retirement lifecycle.");
        int count = await ReadProgressAsync(batch, token).ConfigureAwait(false);
        CloudRemoteApplyResult? recoveredResult = null;
        // No later native entry may overtake an unsettled product entry after a crash.
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            var intent = await ReadOriginalAsync(instance, batch, ordinal, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Applied SDK progress has no original product move admission.");
            if (await IsSettledAsync(intent.OperationId, token).ConfigureAwait(false)) continue;
            if (ordinal != count - 1)
                throw new InvalidDataException("A later SDK entry overtook an unsettled product move.");
            await SettleAsync(intent, instance, batch, ordinal, null, conflicts, token).ConfigureAwait(false);
        }
        if (count < batch.Changes.Count)
        {
            var change = batch.Changes[count];
            var intent = await ReadOriginalAsync(instance, batch, count, token).ConfigureAwait(false);
            if (intent is null)
            {
                string sourcePath = change.PreviousRelativePath!;
                int separator = change.RelativePath.LastIndexOf('\\');
                if (separator <= 0) throw new InvalidDataException("A remote member move cannot replace a managed root.");
                var child = await _members.InspectAsync(sourcePath, change.ItemKind == CloudItemKind.Directory, token).ConfigureAwait(false);
                var parent = await _members.InspectAsync(change.RelativePath[..separator], true, token).ConfigureAwait(false);
                if (child.NativeItemId != change.ItemId || child.RootId != parent.RootId)
                    throw new InvalidDataException("The original remote move does not identify its registered source and target root.");
                intent = await _moves.PrepareRemoteAsync(Guid.NewGuid(), child.MemberId, parent.MemberId,
                    change.RelativePath[(separator + 1)..], instance, batch, count, token).ConfigureAwait(false);
            }
            if (await IsSettledAsync(intent.OperationId, token).ConfigureAwait(false))
                throw new InvalidDataException("Product move settlement is ahead of its original SDK progress.");
            if (await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token).ConfigureAwait(false) is null)
            {
                await _moves.BeginRemoteAsync(intent.OperationId, instance, batch, count, token).ConfigureAwait(false);
                if (checkpoint is not null)
                    await checkpoint(MirrorPulseRemoteNamespaceBatchCheckpoint.AfterMoveStart, token).ConfigureAwait(false);
            }
            else
                recoveredResult = await _moves.ResumeUnmovedRemoteAsync(intent.OperationId, instance, batch, count, options, token).ConfigureAwait(false);
        }
        var result = recoveredResult ?? await actor.RunAsync(async admitted =>
        {
            CloudRemoteApplyResult applied = null!;
            await execution.RunNamespaceOperationAsync(async () => applied = await fileSystem.ApplyRemoteChangesAsync(
                batch, options with { MaximumEntries = 1 }, admitted).ConfigureAwait(false)).ConfigureAwait(false);
            return applied;
        }, token).ConfigureAwait(false);
        if (conflicts is not null) await conflicts.CaptureResultAsync(instance, result, token).ConfigureAwait(false);
        if (checkpoint is not null)
            await checkpoint(MirrorPulseRemoteNamespaceBatchCheckpoint.AfterSdkApply, token).ConfigureAwait(false);
        if (count < batch.Changes.Count && result.AppliedEntryCount > count)
        {
            if (result.AppliedEntryCount != count + 1)
                throw new InvalidDataException("The SDK crossed more than one managed entry boundary.");
            var intent = await ReadOriginalAsync(instance, batch, count, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The SDK entry lost its original product admission.");
            await SettleAsync(intent, instance, batch, count, result, conflicts, token).ConfigureAwait(false);
        }
        return result; // Preserve the actual public SDK result, including partial progress and failures.
    }

    private async ValueTask<int> ReadProgressAsync(CloudRemoteChangeBatch batch, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
        if (progress is not null && (progress.TotalEntryCount != batch.Changes.Count ||
            !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) ||
            progress.AppliedEntryCount < 0 || progress.AppliedEntryCount > batch.Changes.Count ||
            progress.AppliedEntryCount > 0 && progress.LastAppliedChangeId != batch.Changes[progress.AppliedEntryCount - 1].ChangeId ||
            (progress.Status == CloudRemoteBatchStatus.Applied) != (progress.AppliedEntryCount == batch.Changes.Count)))
            throw new InvalidDataException("The original SDK batch progress disagrees with the managed batch.");
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return progress?.AppliedEntryCount ?? 0;
    }

    private async ValueTask<MirrorPulseNamespaceMemberMoveIntent?> ReadOriginalAsync(InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CancellationToken token)
    {
        var intent = await catalog.ReadNamespaceRemoteMoveAsync(instance, batch.BatchId, ordinal, token).ConfigureAwait(false);
        if (intent is null) return null;
        string name = intent.Target.RelativePath[(intent.Target.RelativePath.LastIndexOf('/') + 1)..];
        // Reuse the coordinator's full public-change/reference validation, including terminal history.
        return await _moves.PrepareRemoteAsync(intent.OperationId, intent.MemberId, intent.Target.ParentMemberId,
            name, instance, batch, ordinal, token).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsSettledAsync(Guid operation, CancellationToken token) =>
        await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, token).ConfigureAwait(false) is not null ||
        await catalog.ReadNamespaceRemoteMoveRejectionAsync(operation, token).ConfigureAwait(false) is not null;

    private async ValueTask SettleAsync(MirrorPulseNamespaceMemberMoveIntent intent, InstanceId instance,
        CloudRemoteChangeBatch batch, int ordinal, CloudRemoteApplyResult? result, MirrorPulseRemoteConflictProjector? conflicts, CancellationToken token)
    {
        Guid? conflict = result?.ConflictIds.SingleOrDefault();
        if (result is null)
        {
            // The preview exposes decoded conflict records, not a batch-scoped page query.
            // Match the complete public change rather than interpreting its private payload.
            var start = await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Applied remote progress lost its original before-native start.");
            var records = await execution.RunNormalUserOperationAsync(async () =>
                await fileSystem.ListRemoteConflictsAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            var matching = records.Where(record => record.Conflict.DetectedAt >= start.StartedAt && Matches(batch, ordinal, record.Conflict.Change)).ToArray();
            if (matching.Length > 1) throw new InvalidDataException("The original remote move has ambiguous durable conflicts.");
            if (matching.Length == 1)
            {
                conflict = matching[0].ConflictId;
                if (conflicts is not null) await conflicts.CaptureAsync(instance, matching[0].ConflictId, matching[0].Conflict, token).ConfigureAwait(false);
            }
        }
        if (conflict is { } id && id != Guid.Empty)
            await _moves.RejectRemoteAsync(intent.OperationId, instance, batch, ordinal, id, token).ConfigureAwait(false);
        else
            await _moves.SettleRemoteAsync(intent.OperationId, instance, batch, ordinal, token).ConfigureAwait(false);
    }

    private static bool Matches(CloudRemoteChangeBatch batch, int ordinal, CloudRemoteChange change)
    {
        if (batch.Changes[ordinal].ChangeId != change.ChangeId) return false;
        var changes = batch.Changes.ToArray(); changes[ordinal] = change;
        return new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, changes, batch.FinalCursor)
            .Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span);
    }
}
