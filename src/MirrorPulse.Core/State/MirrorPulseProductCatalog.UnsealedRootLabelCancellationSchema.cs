using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] UnsealedRootLabelCancellationSchema =
    [
        ("table", "root_label_unsealed_cancellations", """
            CREATE TABLE root_label_unsealed_cancellations(
                operation_id TEXT PRIMARY KEY REFERENCES managed_root_rename_history(operation_id),
                root_id TEXT NOT NULL,payload BLOB NOT NULL CHECK(length(payload)<=16777216),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("index", "root_label_unsealed_cancellations_root", "CREATE INDEX root_label_unsealed_cancellations_root ON root_label_unsealed_cancellations(root_id,operation_id)")
    ];

    private static async Task EnsureUnsealedRootLabelCancellationSchemaAsync(SqliteConnection connection, long originalVersion,
        bool stampVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name='root_label_unsealed_cancellations') OR (type='index' AND name='root_label_unsealed_cancellations_root');";
        if (originalVersion >= 92 && (long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))! != 2)
            throw new InvalidDataException("Schema92 lost original unsealed Label cancellation ownership or its root-leading index.");
        foreach (var definition in UnsealedRootLabelCancellationSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                query.Parameters.Clear(); query.CommandText = definition.Sql;
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("Original unsealed cancellation ownership cannot adopt another schema definition.");
        }
        if (originalVersion < 92)
        {
            query.Parameters.Clear(); query.CommandText = """
                SELECT 1 FROM root_label_unsealed_cancellations
                UNION ALL SELECT 1 FROM managed_root_rename_history WHERE json_extract(payload,'$.UnsealedCancellationOperationId') IS NOT NULL
                LIMIT 1;
                """;
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent unsealed Label cancellation ownership.");
            if (stampVersion)
            {
                query.CommandText = "PRAGMA user_version=92;";
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        transaction.Commit();
    }
}
