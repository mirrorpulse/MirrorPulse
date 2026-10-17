using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    public async Task SaveRootPendingRemoteBatchAsync(MirrorPulsePendingRemoteBatchRecord record, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.InstanceId.Value == Guid.Empty || record.RootId is null || record.RootId.Value.Value == Guid.Empty ||
            !record.BatchId.StartsWith(record.InstanceId + "/poll-root/" + record.RootId + "/", StringComparison.Ordinal) ||
            record.Payload is not { Length: > 0 and <= 32 * 1024 * 1024 } ||
            record.CandidateSnapshot is not { Length: > 0 and <= 32 * 1024 * 1024 })
            throw new InvalidDataException("A root polling batch requires its original instance, root and bounded payload.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO root_pending_remote_batches(instance_id,root_id,batch_id,payload,candidate_snapshot)
                VALUES($instance,$root,$batch,$payload,$snapshot) ON CONFLICT(instance_id,root_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$instance", record.InstanceId.ToString());
            insert.Parameters.AddWithValue("$root", record.RootId.ToString());
            insert.Parameters.AddWithValue("$batch", record.BatchId);
            insert.Parameters.AddWithValue("$payload", record.Payload);
            insert.Parameters.AddWithValue("$snapshot", record.CandidateSnapshot);
            if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1) return;
            var existing = await ReadRootPendingRemoteBatchCoreAsync(record.InstanceId, record.RootId.Value, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retained root polling batch disappeared.");
            if (existing.BatchId != record.BatchId || !existing.Payload.AsSpan().SequenceEqual(record.Payload) ||
                !existing.CandidateSnapshot.AsSpan().SequenceEqual(record.CandidateSnapshot))
                throw new InvalidDataException("The root already owns a different immutable polling batch.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulsePendingRemoteBatchRecord?> ReadRootPendingRemoteBatchAsync(InstanceId instance, RootId root,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadRootPendingRemoteBatchCoreAsync(instance, root, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulsePendingRemoteBatchRecord?> ReadLegacyPendingRemoteBatchAsync(InstanceId instance, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPendingRemoteBatchCoreAsync(instance, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task ClearRootPendingRemoteBatchAsync(InstanceId instance, RootId root, string batchId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var record = await ReadRootPendingRemoteBatchCoreAsync(instance, root, token).ConfigureAwait(false);
            if (record is null) return;
            if (record.BatchId != batchId) throw new InvalidDataException("Cannot clear another root polling batch.");
            await using var delete = _connection.CreateCommand();
            delete.CommandText = "DELETE FROM root_pending_remote_batches WHERE instance_id=$instance AND root_id=$root AND batch_id=$batch;";
            delete.Parameters.AddWithValue("$instance", instance.ToString()); delete.Parameters.AddWithValue("$root", root.ToString());
            delete.Parameters.AddWithValue("$batch", batchId);
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulsePendingRemoteBatchRecord?> ReadRootPendingRemoteBatchCoreAsync(InstanceId instance, RootId root, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.CommandText = "SELECT batch_id,payload,candidate_snapshot FROM root_pending_remote_batches WHERE instance_id=$instance AND root_id=$root;";
        query.Parameters.AddWithValue("$instance", instance.ToString()); query.Parameters.AddWithValue("$root", root.ToString());
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(instance, reader.GetString(0), (byte[])reader.GetValue(1), (byte[])reader.GetValue(2)) { RootId = root } : null;
    }

    private async Task<MirrorPulsePendingRemoteBatchRecord?> ReadAnyRootPendingRemoteBatchCoreAsync(InstanceId instance, CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.CommandText = "SELECT root_id FROM root_pending_remote_batches WHERE instance_id=$instance ORDER BY root_id LIMIT 1;";
        query.Parameters.AddWithValue("$instance", instance.ToString());
        return await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string root
            ? await ReadRootPendingRemoteBatchCoreAsync(instance, RootId.Parse(root), token).ConfigureAwait(false) : null;
    }

    private static async Task EnsureRootRemotePollingSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS root_pending_remote_batches (
                instance_id TEXT NOT NULL, root_id TEXT NOT NULL, batch_id TEXT NOT NULL,
                payload BLOB NOT NULL, candidate_snapshot BLOB NOT NULL,
                PRIMARY KEY(instance_id,root_id), UNIQUE(batch_id)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
