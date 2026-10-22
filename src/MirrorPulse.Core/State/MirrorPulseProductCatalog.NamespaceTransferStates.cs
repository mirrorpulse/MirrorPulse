using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task ValidateNamespaceTransferStateAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT s.completed,s.payload_fingerprint=t.fingerprint FROM namespace_transfer_states s
            JOIN namespace_transfers t USING(operation_id) WHERE s.operation_id=$id;
            """;
        query.Parameters.AddWithValue("$id", copy.Plan.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(0) != (copy.Acknowledgement?.AcknowledgedAt is null ? 0 : 1) ||
            reader.GetInt32(1) != 1)
            throw new InvalidDataException("The transfer lost its exact pending or completed history partition.");
    }

    private async Task RequireNamespaceTransferStateIntegrityAsync(SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT 1 FROM namespace_transfers t LEFT JOIN namespace_transfer_states s USING(operation_id)
            WHERE s.operation_id IS NULL OR s.payload_fingerprint<>t.fingerprint OR s.completed<>
                CASE WHEN json_extract(CAST(t.payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL THEN 0 ELSE 1 END
            UNION ALL SELECT 1 FROM namespace_transfer_states s LEFT JOIN namespace_transfers t USING(operation_id) WHERE t.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_transfer_roots r LEFT JOIN namespace_transfer_states s USING(operation_id)
                WHERE s.operation_id IS NULL OR s.completed<>0
            UNION ALL SELECT 1 FROM namespace_transfer_effects e LEFT JOIN namespace_transfers t USING(operation_id) WHERE t.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_transfer_target_births b LEFT JOIN namespace_transfers t ON t.operation_id=b.transfer_id WHERE t.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_transfer_journal_members j LEFT JOIN namespace_transfers t ON t.operation_id=j.transfer_id WHERE t.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_transfers t
                WHERE (SELECT COUNT(*) FROM namespace_transfer_journal_members j WHERE j.transfer_id=t.operation_id)<>
                    COALESCE(json_array_length(CAST(t.payload AS TEXT),'$.JournalClaim.Members'),0)
            UNION ALL SELECT 1 FROM namespace_transfers t,json_each(CAST(t.payload AS TEXT),'$.JournalClaim.Members') m
                LEFT JOIN namespace_transfer_journal_members j ON j.transfer_id=t.operation_id AND j.operation_id=json_extract(m.value,'$.OperationId')
                WHERE j.operation_id IS NULL OR j.sequence<>json_extract(m.value,'$.Sequence')
            LIMIT 1;
            """;
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Transfer recovery found a missing, orphaned or contradictory history, journal or pending partition.");
    }

    private async Task EnsureNamespaceTransferStateSchemaAsync(long originalVersion, CancellationToken token)
    {
        using var transaction = _connection.BeginTransaction(); await using var schema = _connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='namespace_transfer_states';";
        bool exists = (long)(await schema.ExecuteScalarAsync(token).ConfigureAwait(false))! == 1;
        if (originalVersion >= 83 && !exists)
            throw new InvalidDataException("Schema83 lost its pending/completed transfer partition; empty history cannot replace it.");
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_transfer_states(
                operation_id TEXT PRIMARY KEY REFERENCES namespace_transfers(operation_id),
                completed INTEGER NOT NULL CHECK(completed IN (0,1)),payload_fingerprint BLOB NOT NULL CHECK(length(payload_fingerprint)=32));
            CREATE INDEX IF NOT EXISTS namespace_transfer_pending ON namespace_transfer_states(completed,operation_id);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (originalVersion < 83)
        {
            // Older Hosts never completed a transfer. Preserve every original payload and fingerprint.
            // Validate all original pending references before committing the new partition or version.
            schema.CommandText = """
                SELECT 1 FROM namespace_transfers WHERE json_type(CAST(payload AS TEXT),'$.Acknowledgement') IS NOT NULL LIMIT 1;
                """;
            if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A legacy transfer cannot manufacture a later acknowledgement history.");
            if (!exists)
            {
                schema.CommandText = "INSERT INTO namespace_transfer_states(operation_id,completed,payload_fingerprint) SELECT operation_id,0,fingerprint FROM namespace_transfers;";
                await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            _ = await ReadPendingNamespaceTransfersCoreAsync(transaction, token).ConfigureAwait(false);
            schema.CommandText = "PRAGMA user_version=83;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
