using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] DirectoryAcceptanceSchema =
    [
        ("table", "directory_acceptances", """
            CREATE TABLE directory_acceptances(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                previous_operation_id TEXT NULL REFERENCES directory_acceptances(operation_id),
                payload BLOB NOT NULL CHECK(length(payload)<=65536),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                receipt BLOB NULL CHECK(length(receipt)<=65536),
                receipt_fingerprint BLOB NULL CHECK(length(receipt_fingerprint)=32),
                CHECK((receipt IS NULL)=(receipt_fingerprint IS NULL)))
            """),
        ("index", "ux_directory_identity_first", "CREATE UNIQUE INDEX ux_directory_identity_first ON directory_acceptances(member_id) WHERE previous_operation_id IS NULL AND receipt IS NOT NULL"),
        ("index", "ux_directory_identity_previous", "CREATE UNIQUE INDEX ux_directory_identity_previous ON directory_acceptances(previous_operation_id) WHERE previous_operation_id IS NOT NULL AND receipt IS NOT NULL"),
        ("index", "ix_directory_identity_member", "CREATE INDEX ix_directory_identity_member ON directory_acceptances(member_id)")
    ];

    private static async Task EnsureDirectoryAcceptanceSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in DirectoryAcceptanceSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            var existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 104) throw new InvalidDataException("Schema104 lost its original directory acceptance ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || !MatchesDirectoryAcceptanceSchema(sql, definition.Name, definition.Sql, originalVersion))
                throw new InvalidDataException("Directory acceptance cannot adopt another schema definition.");
        }
        if (originalVersion < 104)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM directory_acceptances LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent original directory confirmations.");
            query.CommandText = "PRAGMA user_version=104;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private static bool MatchesDirectoryAcceptanceSchema(string sql, string name, string original, long version)
    {
        string linked = name switch
        {
            "directory_acceptances" => DirectoryAcceptanceTableWithMoveLink(original),
            "ux_directory_identity_first" => original + " AND previous_move_id IS NULL",
            _ => original,
        };
        string existing = NormalizeRetiredTransferIndexSql(sql);
        return existing == NormalizeRetiredTransferIndexSql(linked) ||
            version < 110 && existing == NormalizeRetiredTransferIndexSql(original);
    }
}
