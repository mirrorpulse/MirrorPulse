using System.Text.Json;
using System.Text.Json.Serialization;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains public incoming batches before SDK access and converges an earlier instance batch before later work.</summary>
/// <remarks>The Host's instance/library owner serializes calls. This boundary neither creates placeholders nor
/// interprets private SDK payloads. Native/member recovery and role admission remain with the actual apply path.</remarks>
internal sealed class MirrorPulseRetainedRemoteBatchCoordinator(MirrorPulseProductCatalog catalog, ICloudStateStore store,
    MirrorPulseRootRouter router,
    Func<InstanceId, CloudRemoteChangeBatch, CloudRemoteApplyOptions, CancellationToken, ValueTask<MirrorPulseRemotePollApplyOutcome>> apply)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 32 };

    private sealed record ApplyPolicy(int MaximumEntries, bool Preserve, bool Suppress, long LifetimeTicks, int ObservationCount);
    private sealed record RootMapping(string RootId, string RootKey, string DirectoryName, RootIdentityScope IdentityScope);
    private sealed record Entry(string ChangeId, CloudRemoteChangeKind Kind, string RemoteId, string RemoteRevision,
        CloudItemKind ItemKind, string RelativePath, Guid? ItemId, string? PreviousRevision, string? PreviousPath,
        long? Length, MirrorPulseRemoteSnapshotMetadata? Metadata, byte[] CursorAfter);
    private sealed record Envelope(int Version, string InstanceId, string BatchId, byte[] InitialCursor, byte[] FinalCursor,
        byte[] Fingerprint, Entry[] Changes, ApplyPolicy Policy, RootMapping[] Roots);

    internal async ValueTask<MirrorPulseRemotePollApplyOutcome> ApplyAsync(InstanceId instance, CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(options);
        ValidatePolicy(options);
        ValidateBatchScope(instance, batch);
        var pollingRoot = MirrorPulseRemotePollBatchScope.Read(instance, batch);
        var pending = await catalog.ReadRetainedRemoteBatchAsync(instance, token).ConfigureAwait(false);
        if (pending is not null)
        {
            var (original, originalBatch, originalOptions) = Decode(pending);
            bool same = pending.BatchId == batch.BatchId;
            if (same && (!originalBatch.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) || original.Policy != Policy(options)))
                throw new InvalidDataException("The incoming replay changed its original batch or public apply policy.");
            var previous = same ? await ReplayAsync(pending, original, originalBatch, originalOptions, token).ConfigureAwait(false) :
                await ResumeToBoundaryAsync(pending, original, originalBatch, originalOptions, token).ConfigureAwait(false);
            if (same) return previous;
            if (!previous.Completed) throw new InvalidOperationException("The original incoming batch must converge before a later batch can apply.");
        }
        // Root-scoped polling already has its immutable product intent and independent cursor.
        // It still waits for an older incoming batch above, without occupying the streamed slot.
        if (pollingRoot is not null)
            return await apply(instance, batch, options, token).ConfigureAwait(false);
        var captured = Capture(instance, batch, options);
        var retained = new MirrorPulseRetainedRemoteBatch(instance, batch.BatchId, JsonSerializer.SerializeToUtf8Bytes(captured, Options));
        await catalog.RetainRemoteBatchAsync(retained, token).ConfigureAwait(false);
        return await ReplayAsync(retained, captured, batch, options, token).ConfigureAwait(false);
    }

    internal async ValueTask<bool> ResumeAsync(InstanceId instance, CancellationToken token)
    {
        var pending = await catalog.ReadRetainedRemoteBatchAsync(instance, token).ConfigureAwait(false);
        if (pending is null) return true;
        var (envelope, batch, options) = Decode(pending);
        return (await ResumeToBoundaryAsync(pending, envelope, batch, options, token).ConfigureAwait(false)).Completed;
    }

    private async ValueTask<MirrorPulseRemotePollApplyOutcome> ResumeToBoundaryAsync(MirrorPulseRetainedRemoteBatch retained,
        Envelope envelope, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions options, CancellationToken token)
    {
        var previous = await ReadPrefixAsync(batch, token).ConfigureAwait(false);
        for (int turn = 0; ; turn++)
        {
            var result = await ReplayAsync(retained, envelope, batch, options, token).ConfigureAwait(false);
            if (result.Completed || turn == 63) return result;
            int current = await ReadPrefixAsync(batch, token).ConfigureAwait(false);
            if (current < previous) throw new InvalidDataException("The original SDK batch prefix moved backwards during recovery.");
            if (current == previous) return result; // A real retry must not become a busy redispatch loop.
            previous = current;
        }
    }

    private async Task<int> ReadPrefixAsync(CloudRemoteChangeBatch batch, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
        var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
        if (progress is not null && (progress.TotalEntryCount != batch.Changes.Count || progress.AppliedEntryCount < 0 ||
            progress.AppliedEntryCount > batch.Changes.Count || !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span)))
            throw new InvalidDataException("The original SDK progress cannot be used to schedule another recovery turn.");
        await transaction.RollbackAsync(token).ConfigureAwait(false); return progress?.AppliedEntryCount ?? 0;
    }

    private async ValueTask<MirrorPulseRemotePollApplyOutcome> ReplayAsync(MirrorPulseRetainedRemoteBatch retained,
        Envelope original, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions options, CancellationToken token)
    {
        if (!original.Roots.SequenceEqual(CaptureMappings(retained.InstanceId, batch)))
            throw new InvalidDataException("The retained incoming batch requires its original stable root mapping and names.");
        var result = await apply(retained.InstanceId, batch, options, token).ConfigureAwait(false);
        if (!result.Completed) return result;
        if (!result.SafeCursor.Span.SequenceEqual(batch.FinalCursor.Span))
            throw new InvalidDataException("A completed incoming batch reported an incomplete cursor.");
        await using (var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
            var cursor = await transaction.Checkpoints.GetAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(retained.InstanceId), token).ConfigureAwait(false);
            if (progress is null || progress.Status != CloudRemoteBatchStatus.Applied || progress.AppliedEntryCount != batch.Changes.Count ||
                progress.TotalEntryCount != batch.Changes.Count || !progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) ||
                !progress.Cursor.Span.SequenceEqual(batch.FinalCursor.Span) ||
                progress.LastAppliedChangeId != (batch.Changes.Count == 0 ? null : batch.Changes[^1].ChangeId) || cursor is null ||
                !cursor.Value.Span.SequenceEqual(batch.FinalCursor.Span))
                throw new InvalidDataException("The incoming batch is not durably complete at its original SDK/product cursor boundary.");
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }
        await catalog.CompleteRetainedRemoteBatchAsync(retained, token).ConfigureAwait(false);
        return result;
    }

    private Envelope Capture(InstanceId instance, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions options)
    {
        ValidateBatchScope(instance, batch);
        return new(1, instance.ToString(), batch.BatchId, batch.InitialCursor.ToArray(), batch.FinalCursor.ToArray(), batch.Fingerprint.ToArray(),
            batch.Changes.Select(change => new Entry(change.ChangeId, change.Kind, change.RemoteId, change.RemoteRevision, change.ItemKind,
                change.RelativePath, change.ItemId, change.PreviousRemoteRevision, change.PreviousRelativePath, change.Length,
                change.Metadata is { } metadata ? new(metadata.Kind, metadata.Attributes, metadata.CreationTime, metadata.LastAccessTime,
                    metadata.LastWriteTime, metadata.ChangeTime) : null, change.CursorAfter.ToArray())).ToArray(), Policy(options), CaptureMappings(instance, batch));
    }

    private RootMapping[] CaptureMappings(InstanceId instance, CloudRemoteChangeBatch batch)
    {
        IEnumerable<RootRegistration> selected = batch.Changes.Count == 0 ? router.Registrations.Where(root => root.InstanceId == instance) :
            batch.Changes.SelectMany(change => change.PreviousRelativePath is { } previous ? new[] { change.RelativePath, previous } : [change.RelativePath])
                .Select(path =>
                {
                    var route = router.ResolveCurrentPath(path);
                    if (route.InstanceId != instance) throw new InvalidDataException("An incoming batch crosses its Adapter instance.");
                    return router.GetRegistration(instance, route.RootKey);
                });
        var roots = selected.DistinctBy(root => root.RootId).OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal).ToArray();
        if (roots.Length is 0 or > 4096 || roots.Any(root => root.State != RootRegistrationState.Active))
            throw new InvalidOperationException("Incoming batch replay requires its original active roots.");
        return roots.Select(root => new RootMapping(root.RootId.ToString(), root.UniquenessKey, root.DirectoryName, root.IdentityScope)).ToArray();
    }

    private static (Envelope Envelope, CloudRemoteChangeBatch Batch, CloudRemoteApplyOptions Options) Decode(MirrorPulseRetainedRemoteBatch retained)
    {
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(retained.Payload, Options) ?? throw new InvalidDataException("The incoming envelope is empty.");
            if (envelope.Version != 1 || envelope.InstanceId != retained.InstanceId.ToString() || envelope.BatchId != retained.BatchId ||
                envelope.Fingerprint is not { Length: 32 } || envelope.Changes is null || envelope.Changes.Length > 65_536 ||
                envelope.Roots is not { Length: > 0 and <= 4096 } || envelope.Policy is null)
                throw new InvalidDataException("The incoming envelope has an unsupported shape or scope.");
            var policy = envelope.Policy;
            var options = new CloudRemoteApplyOptions
            {
                MaximumEntries = policy.MaximumEntries,
                PreserveUnsynchronizedLocalContent = policy.Preserve,
                SuppressLocalEcho = policy.Suppress,
                EchoSuppressionLifetime = TimeSpan.FromTicks(policy.LifetimeTicks),
                EchoSuppressionObservationCount = policy.ObservationCount
            };
            ValidatePolicy(options);
            var changes = envelope.Changes.Select(change => new CloudRemoteChange(change.ChangeId, change.Kind, change.RemoteId,
                change.RemoteRevision, change.ItemKind, change.RelativePath, change.ItemId, change.PreviousRevision, change.PreviousPath,
                change.Length, Metadata(change.Metadata), change.CursorAfter)).ToArray();
            var batch = new CloudRemoteChangeBatch(envelope.BatchId, envelope.InitialCursor, changes, envelope.FinalCursor);
            if (!batch.Fingerprint.Span.SequenceEqual(envelope.Fingerprint) ||
                !JsonSerializer.SerializeToUtf8Bytes(envelope, Options).AsSpan().SequenceEqual(retained.Payload))
                throw new InvalidDataException("The original public SDK batch fingerprint or canonical envelope changed.");
            return (envelope, batch, options);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("The retained public batch cannot be reconstructed safely.", error); }
    }

    private static CloudPlaceholderMetadata? Metadata(MirrorPulseRemoteSnapshotMetadata? source)
    {
        if (source is null) return null;
        var builder = source.Kind == CloudItemKind.Directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder() : CloudPlaceholderMetadata.CreateFileBuilder();
        builder.WithAttributes(source.Attributes);
        if (source.CreationTime is { } created) builder.WithCreationTime(created);
        if (source.LastAccessTime is { } accessed) builder.WithLastAccessTime(accessed);
        if (source.LastWriteTime is { } written) builder.WithLastWriteTime(written);
        if (source.ChangeTime is { } changed) builder.WithChangeTime(changed);
        return builder.Build();
    }

    private static ApplyPolicy Policy(CloudRemoteApplyOptions options) => new(options.MaximumEntries, options.PreserveUnsynchronizedLocalContent,
        options.SuppressLocalEcho, options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount);
    private static void ValidateBatchScope(InstanceId instance, CloudRemoteChangeBatch batch)
    {
        if (instance.Value == Guid.Empty || batch.BatchId.Length > 16_384 ||
            !batch.BatchId.StartsWith(instance + "/", StringComparison.Ordinal) || batch.Changes.Count > 65_536)
            throw new InvalidDataException("Incoming replay requires a bounded batch belonging to its original instance.");
    }
    private static void ValidatePolicy(CloudRemoteApplyOptions options)
    {
        if (options.ConflictResolver is not null || options.MaximumEntries is < 1 or > 65_536 ||
            options.EchoSuppressionObservationCount is < 1 or > 64 || options.EchoSuppressionLifetime <= TimeSpan.Zero || options.EchoSuppressionLifetime > TimeSpan.FromHours(1))
            throw new NotSupportedException("Durable incoming replay requires a bounded public policy without a transient conflict callback.");
    }
}
