using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string RemoteRetirementItemIndexSql = """
        CREATE INDEX namespace_remote_retirement_items ON namespace_member_retirements(
            json_extract(CAST(payload AS TEXT),'$.Intent.RemoteEntry.ItemId'),operation_id)
        WHERE json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NOT NULL
        """;

    private static async Task EnsureRemoteRetirementGenerationSchemaAsync(SqliteConnection connection,
        long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='namespace_remote_retirement_items';";
        var existing = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (originalVersion >= 88) throw new InvalidDataException("Schema88 lost its bounded original remote-retirement lookup.");
            // Index historical originals without manufacturing a settled name epoch or changing their bytes.
            schema.CommandText = RemoteRetirementItemIndexSql;
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(RemoteRetirementItemIndexSql))
            throw new InvalidDataException("The remote-retirement index no longer identifies the original public item.");
        if (originalVersion < 88)
        {
            schema.CommandText = "PRAGMA user_version=88;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
