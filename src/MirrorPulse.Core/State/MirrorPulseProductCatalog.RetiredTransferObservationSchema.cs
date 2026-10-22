using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string RetiredTransferSourceIndexSql = """
        CREATE INDEX namespace_transfer_source_items ON namespace_member_retirements(
            json_extract(CAST(payload AS TEXT),'$.StartedItemId'),operation_id)
        WHERE json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL
        """;
    private const string RetiredTransferPendingIndexSql = "CREATE INDEX retired_transfer_observation_pending ON retired_transfer_observations(status,source_root_id,started_utc,operation_id)";

    private static string NormalizeRetiredTransferIndexSql(string sql) =>
        string.Join(" ", sql.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task EnsureRetiredTransferObservationSchemaAsync(SqliteConnection connection,
        long originalVersion, bool stampVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name='retired_transfer_observations') OR (type='index' AND name IN ('namespace_transfer_source_items','retired_transfer_observation_pending'));";
        if (originalVersion >= 85 && (long)(await schema.ExecuteScalarAsync(token).ConfigureAwait(false))! != 3)
            throw new InvalidDataException("Schema85 lost its original notification ownership or bounded generation lookup; empty history cannot replace it.");
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS retired_transfer_observations(
                operation_id TEXT PRIMARY KEY,transfer_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),
                retirement_id TEXT NOT NULL REFERENCES namespace_member_retirements(operation_id),source_root_id TEXT NOT NULL,
                started_utc TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN (0,1)),payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE INDEX IF NOT EXISTS retired_transfer_observation_pending
                ON retired_transfer_observations(status,source_root_id,started_utc,operation_id);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='namespace_transfer_source_items';";
        object? indexSql = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (indexSql is null)
        {
            // SQLite indexes existing canonical records without rewriting original retirement bytes.
            schema.CommandText = RetiredTransferSourceIndexSql;
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (NormalizeRetiredTransferIndexSql(indexSql.ToString()!) != NormalizeRetiredTransferIndexSql(RetiredTransferSourceIndexSql))
            throw new InvalidDataException("The source-generation index no longer describes the retained original item and transfer owner.");
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='retired_transfer_observation_pending';";
        if (NormalizeRetiredTransferIndexSql((string)(await schema.ExecuteScalarAsync(token).ConfigureAwait(false))!) != NormalizeRetiredTransferIndexSql(RetiredTransferPendingIndexSql))
            throw new InvalidDataException("The original notification recovery index lost its pending root and first-start order.");
        if (originalVersion < 85)
        {
            schema.CommandText = "SELECT 1 FROM retired_transfer_observations LIMIT 1;";
            if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older schema cannot invent later original-notification ACK ownership.");
            if (stampVersion)
            {
                schema.CommandText = "PRAGMA user_version=85;";
                await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        transaction.Commit();
    }

    private async Task RequireSoleRetiredTransferSourceAsync(Guid itemId, Guid retirementId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT operation_id FROM namespace_member_retirements INDEXED BY namespace_transfer_source_items
            WHERE json_extract(CAST(payload AS TEXT),'$.StartedItemId')=$item
                AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL
            ORDER BY operation_id LIMIT 2;
            """;
        query.Parameters.AddWithValue("$item", itemId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != retirementId.ToString("D") ||
            await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("An ambiguous or reused public item requires explicit generation reconciliation.");
    }
}
