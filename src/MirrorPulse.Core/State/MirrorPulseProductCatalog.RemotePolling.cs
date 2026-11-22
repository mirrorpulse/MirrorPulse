using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Opaque product recovery intent; CfSharp still owns apply state and checkpoints.</summary>
public sealed record MirrorPulsePendingRemoteBatchRecord(
    InstanceId InstanceId, string BatchId, byte[] Payload, byte[] CandidateSnapshot)
{
    public RootId? RootId { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task SavePendingRemoteBatchAsync(MirrorPulsePendingRemoteBatchRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RootId is not null) throw new InvalidDataException("Use the root-scoped boundary for a root polling batch.");
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BatchId);
        ArgumentNullException.ThrowIfNull(record.Payload);
        ArgumentNullException.ThrowIfNull(record.CandidateSnapshot);
        if (!record.BatchId.StartsWith(record.InstanceId + "/", StringComparison.Ordinal) ||
            record.Payload.Length is 0 or > 32 * 1024 * 1024 || record.CandidateSnapshot.Length is 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("The pending remote batch scope or size is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO pending_remote_batches(instance_id,batch_id,payload,candidate_snapshot)
                VALUES($instance,$batch,$payload,$snapshot) ON CONFLICT(instance_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$instance", record.InstanceId.ToString());
            insert.Parameters.AddWithValue("$batch", record.BatchId);
            insert.Parameters.AddWithValue("$payload", record.Payload);
            insert.Parameters.AddWithValue("$snapshot", record.CandidateSnapshot);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return;
            MirrorPulsePendingRemoteBatchRecord existing = await ReadPendingRemoteBatchCoreAsync(record.InstanceId, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The pending remote batch disappeared.");
            if (existing.BatchId != record.BatchId || !existing.Payload.AsSpan().SequenceEqual(record.Payload) ||
                !existing.CandidateSnapshot.AsSpan().SequenceEqual(record.CandidateSnapshot))
                throw new InvalidDataException("An instance already has different pending remote intent; replay it first.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulsePendingRemoteBatchRecord?> ReadPendingRemoteBatchAsync(InstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadPendingRemoteBatchCoreAsync(instanceId, cancellationToken).ConfigureAwait(false) ??
                await ReadAnyRootPendingRemoteBatchCoreAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearPendingRemoteBatchAsync(InstanceId instanceId, string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulsePendingRemoteBatchRecord? record = await ReadPendingRemoteBatchCoreAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (record is null) return;
            if (record.BatchId != batchId) throw new InvalidDataException("Cannot clear a different pending batch.");
            await using SqliteCommand delete = _connection.CreateCommand();
            delete.CommandText = "DELETE FROM pending_remote_batches WHERE instance_id=$instance AND batch_id=$batch;";
            delete.Parameters.AddWithValue("$instance", instanceId.ToString());
            delete.Parameters.AddWithValue("$batch", batchId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulsePendingRemoteBatchRecord?> ReadPendingRemoteBatchCoreAsync(
        InstanceId instanceId, CancellationToken cancellationToken)
    {
        await using SqliteCommand select = _connection.CreateCommand();
        select.CommandText = "SELECT batch_id,payload,candidate_snapshot FROM pending_remote_batches WHERE instance_id=$instance;";
        select.Parameters.AddWithValue("$instance", instanceId.ToString());
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(instanceId, reader.GetString(0), (byte[])reader.GetValue(1), (byte[])reader.GetValue(2)) : null;
    }
}
