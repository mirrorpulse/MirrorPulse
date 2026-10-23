using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Immutable input-to-public projection retained even after an incoming batch completes.</summary>
/// <remarks>This is product replay intent, not an SDK progress, cursor, native outcome or permission grant.</remarks>
public sealed record MirrorPulseAdapterRemoteProjection(InstanceId InstanceId, string BatchId, byte[] SourceFingerprint, byte[] Payload);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseAdapterRemoteProjection?> ReadAdapterRemoteProjectionAsync(InstanceId instance, string batchId,
        CancellationToken token = default)
    {
        ValidateAdapterRemoteProjectionKey(instance, batchId);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadAdapterRemoteProjectionCoreAsync(instance, batchId, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task RetainAdapterRemoteProjectionAsync(MirrorPulseAdapterRemoteProjection projection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(projection); ValidateAdapterRemoteProjection(projection);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO adapter_remote_projections(instance_id,batch_id,source_fingerprint,payload,payload_fingerprint)
                VALUES($instance,$batch,$source,$payload,$hash) ON CONFLICT(instance_id,batch_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$instance", projection.InstanceId.ToString()); insert.Parameters.AddWithValue("$batch", projection.BatchId);
            insert.Parameters.AddWithValue("$source", projection.SourceFingerprint); insert.Parameters.AddWithValue("$payload", projection.Payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(projection.Payload));
            if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1) return;
            var original = await ReadAdapterRemoteProjectionCoreAsync(projection.InstanceId, projection.BatchId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original incoming projection disappeared.");
            if (!CryptographicOperations.FixedTimeEquals(original.SourceFingerprint, projection.SourceFingerprint) ||
                !original.Payload.AsSpan().SequenceEqual(projection.Payload))
                throw new InvalidDataException("An incoming batch cannot change its first input or selected public projection.");
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseAdapterRemoteProjection?> ReadAdapterRemoteProjectionCoreAsync(InstanceId instance, string batchId,
        CancellationToken token)
    {
        await using var query = _connection.CreateCommand();
        query.CommandText = "SELECT source_fingerprint,payload,payload_fingerprint FROM adapter_remote_projections WHERE instance_id=$instance AND batch_id=$batch;";
        query.Parameters.AddWithValue("$instance", instance.ToString()); query.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = new MirrorPulseAdapterRemoteProjection(instance, batchId, (byte[])reader[0], (byte[])reader[1]);
        ValidateAdapterRemoteProjection(result); byte[] hash = (byte[])reader[2];
        if (hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(result.Payload)))
            throw new InvalidDataException("The original incoming projection lost its payload integrity.");
        return result;
    }

    private static void ValidateAdapterRemoteProjection(MirrorPulseAdapterRemoteProjection projection)
    {
        ValidateAdapterRemoteProjectionKey(projection.InstanceId, projection.BatchId);
        if (projection.SourceFingerprint is not { Length: 32 } || projection.Payload is not { Length: > 0 and <= 32 * 1024 * 1024 })
            throw new InvalidDataException("An incoming projection requires a bounded payload and exact input fingerprint.");
    }

    private static void ValidateAdapterRemoteProjectionKey(InstanceId instance, string batchId)
    {
        if (instance.Value == Guid.Empty || string.IsNullOrWhiteSpace(batchId) || batchId.Length > 16_384 ||
            !batchId.StartsWith(instance + "/", StringComparison.Ordinal))
            throw new InvalidDataException("An incoming projection requires its original instance and bounded batch identity.");
    }

    private static async Task EnsureAdapterRemoteProjectionSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        const string sql = """
            CREATE TABLE adapter_remote_projections(instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,
                source_fingerprint BLOB NOT NULL CHECK(length(source_fingerprint)=32),
                payload BLOB NOT NULL CHECK(length(payload)>0 AND length(payload)<=33554432),
                payload_fingerprint BLOB NOT NULL CHECK(length(payload_fingerprint)=32),PRIMARY KEY(instance_id,batch_id))
            """;
        using var transaction = connection.BeginTransaction();
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='adapter_remote_projections';";
        var existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (existing is null)
        {
            if (originalVersion >= 87) throw new InvalidDataException("Schema87 lost its durable incoming projections.");
            query.CommandText = sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (NormalizeRetiredTransferIndexSql(existing) != NormalizeRetiredTransferIndexSql(sql))
            throw new InvalidDataException("The incoming projection schema lost its immutable bounded lookup.");
        if (originalVersion < 87)
        {
            query.CommandText = "SELECT 1 FROM adapter_remote_projections LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent incoming projection ownership.");
            query.CommandText = "PRAGMA user_version=87;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
