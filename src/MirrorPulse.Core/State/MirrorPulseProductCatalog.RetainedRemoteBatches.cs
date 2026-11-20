using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Host-owned public batch replay intent; it contains no SDK-private checkpoint or native-success claim.</summary>
public sealed record MirrorPulseRetainedRemoteBatch(InstanceId InstanceId, string BatchId, byte[] Payload);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task RetainRemoteBatchAsync(MirrorPulseRetainedRemoteBatch batch, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateRetainedRemoteBatch(batch);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO retained_remote_batches(instance_id,batch_id,payload,fingerprint)
                VALUES($instance,$batch,$payload,$hash) ON CONFLICT(instance_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$instance", batch.InstanceId.ToString()); insert.Parameters.AddWithValue("$batch", batch.BatchId);
            insert.Parameters.AddWithValue("$payload", batch.Payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(batch.Payload));
            if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1) return;
            var existing = await ReadRetainedRemoteBatchCoreAsync(batch.InstanceId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retained remote batch disappeared.");
            if (existing.BatchId != batch.BatchId || !existing.Payload.AsSpan().SequenceEqual(batch.Payload))
                throw new InvalidDataException("The instance must converge its original retained remote batch first.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRetainedRemoteBatch?> ReadRetainedRemoteBatchAsync(InstanceId instance, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadRetainedRemoteBatchCoreAsync(instance, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Reports a still-retained owner's recovery without clearing rescan, last success or transfer evidence.</summary>
    public async Task MarkRetainedRemoteBatchRecoveryPendingAsync(InstanceId instance, CancellationToken token = default)
    {
        if (instance.Value == Guid.Empty) throw new ArgumentException("A recovery status requires its original instance.", nameof(instance));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var update = _connection.CreateCommand();
            update.CommandText = """
                INSERT INTO instance_runtime(instance_id,phase,requires_full_rescan,last_successful_sync_utc,last_error_code)
                SELECT $instance,'Remote recovery pending',0,NULL,'RemoteBatchRecoveryPending'
                WHERE EXISTS(SELECT 1 FROM retained_remote_batches WHERE instance_id=$instance)
                ON CONFLICT(instance_id) DO UPDATE SET phase=excluded.phase,last_error_code=excluded.last_error_code;
                """;
            update.Parameters.AddWithValue("$instance", instance.ToString());
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Finds at most 64 original instance owners per page, including temporarily disabled owners.</summary>
    public async Task<IReadOnlyList<InstanceId>> ReadRetainedRemoteBatchOwnersAsync(InstanceId? after = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await using var query = _connection.CreateCommand();
            query.CommandText = "SELECT instance_id FROM retained_remote_batches WHERE $after IS NULL OR instance_id>$after ORDER BY instance_id LIMIT 64;";
            query.Parameters.AddWithValue("$after", (object?)after?.ToString() ?? DBNull.Value);
            var result = new List<InstanceId>(); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(InstanceId.Parse(reader.GetString(0)));
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteRetainedRemoteBatchAsync(MirrorPulseRetainedRemoteBatch original, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(original); ValidateRetainedRemoteBatch(original);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var retained = await ReadRetainedRemoteBatchCoreAsync(original.InstanceId, token, transaction).ConfigureAwait(false);
            if (retained is null) { transaction.Commit(); return; }
            if (retained.BatchId != original.BatchId || !retained.Payload.AsSpan().SequenceEqual(original.Payload))
                throw new InvalidDataException("Cannot complete another retained remote batch.");
            await using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM retained_remote_batches WHERE instance_id=$instance AND batch_id=$batch;
                UPDATE instance_runtime SET
                    phase=CASE WHEN phase='Remote recovery pending' THEN
                        CASE WHEN requires_full_rescan=1 THEN 'Rescan pending' ELSE 'Idle' END ELSE phase END,
                    last_error_code=NULL
                WHERE instance_id=$instance AND last_error_code='RemoteBatchRecoveryPending';
                """;
            delete.Parameters.AddWithValue("$instance", original.InstanceId.ToString()); delete.Parameters.AddWithValue("$batch", original.BatchId);
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRetainedRemoteBatch?> ReadRetainedRemoteBatchCoreAsync(InstanceId instance, CancellationToken token,
        SqliteTransaction? transaction = null)
    {
        await using var query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT batch_id,payload,fingerprint FROM retained_remote_batches WHERE instance_id=$instance;";
        query.Parameters.AddWithValue("$instance", instance.ToString());
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = new MirrorPulseRetainedRemoteBatch(instance, reader.GetString(0), (byte[])reader.GetValue(1));
        ValidateRetainedRemoteBatch(result);
        byte[] hash = (byte[])reader.GetValue(2);
        if (hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(result.Payload)))
            throw new InvalidDataException("The retained remote batch payload lost its integrity.");
        return result;
    }

    private static void ValidateRetainedRemoteBatch(MirrorPulseRetainedRemoteBatch batch)
    {
        if (batch.InstanceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(batch.BatchId) || batch.BatchId.Length > 16_384 ||
            !batch.BatchId.StartsWith(batch.InstanceId + "/", StringComparison.Ordinal) || batch.Payload is not { Length: > 0 and <= 32 * 1024 * 1024 })
            throw new InvalidDataException("A retained remote batch requires its original instance, identity and bounded payload.");
    }

    private static async Task EnsureRetainedRemoteBatchSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS retained_remote_batches (
                instance_id TEXT PRIMARY KEY, batch_id TEXT NOT NULL, payload BLOB NOT NULL, fingerprint BLOB NOT NULL
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
