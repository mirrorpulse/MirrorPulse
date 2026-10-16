using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Publishes an Adapter cursor only after CfSharp durably applies its batch. A crash between
/// CfSharp apply and checkpoint publication is repaired by replaying the same batch ID.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteBatchCoordinator
{
    private readonly CloudFileSystem _fileSystem;
    private readonly MirrorPulseCfSharpStateSession _state;
    private readonly MirrorPulseRemoteConflictProjector? _conflicts;

    public MirrorPulseRemoteBatchCoordinator(
        CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state,
        MirrorPulseRemoteConflictProjector? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(state);
        _fileSystem = fileSystem;
        _state = state;
        _conflicts = conflicts;
    }

    public async ValueTask<CloudRemoteApplyResult> ApplyAsync(
        InstanceId instanceId,
        CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!batch.BatchId.StartsWith($"{instanceId}/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A remote batch must be scoped to its Adapter instance.");
        }

        string checkpointName = GetCheckpointName(instanceId);
        await using (ICloudStateTransaction preflight = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            CloudStateCheckpoint? checkpoint = await preflight.Checkpoints
                .GetAsync(checkpointName, cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null &&
                !checkpoint.Value.Span.SequenceEqual(batch.InitialCursor.Span))
            {
                CloudRemoteBatchState? existing = await preflight.RemoteBatches
                    .GetAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
                bool exactReplay = existing is not null &&
                    existing.Status == CloudRemoteBatchStatus.Applied &&
                    existing.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) &&
                    checkpoint.Value.Span.SequenceEqual(batch.FinalCursor.Span);
                if (!exactReplay)
                {
                    throw new InvalidDataException("The Adapter batch does not follow its durable CfSharp cursor.");
                }
            }

            await preflight.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        CloudRemoteApplyResult result = await _fileSystem.ApplyRemoteChangesAsync(
            batch, options ?? CloudRemoteApplyOptions.Default, cancellationToken).ConfigureAwait(false);
        if (_conflicts is not null)
        {
            await _conflicts.CaptureResultAsync(instanceId, result, cancellationToken).ConfigureAwait(false);
        }

        if (result.RequiresRetry || result.Status != CloudRemoteBatchStatus.Applied)
        {
            return result;
        }

        if (!result.SafeCursor.Span.SequenceEqual(batch.FinalCursor.Span))
        {
            throw new InvalidDataException("CfSharp reported an applied batch with an incomplete safe cursor.");
        }

        await using ICloudStateTransaction publish = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await publish.Checkpoints.UpsertAsync(
            new CloudStateCheckpoint(checkpointName, result.SafeCursor.Span, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await publish.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadCursorAsync(
        InstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        await using ICloudStateTransaction transaction = await _state.OpenStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints
            .GetAsync(GetCheckpointName(instanceId), cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return null;
        return new ReadOnlyMemory<byte>(checkpoint.Value.ToArray());
    }

    public static string GetCheckpointName(InstanceId instanceId) =>
        $"mirrorpulse/adapter/{instanceId}/remote-cursor";
}
