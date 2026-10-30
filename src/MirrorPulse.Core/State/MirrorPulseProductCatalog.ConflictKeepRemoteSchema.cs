using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] ConflictKeepRemoteSchema =
    [
        ("table", "conflict_keep_remote_preparations", """
            CREATE TABLE conflict_keep_remote_preparations(
                command_id TEXT NOT NULL PRIMARY KEY REFERENCES user_commands(command_id),
                payload BLOB NOT NULL CHECK(length(payload)<=16777216),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("table", "conflict_keep_remote_members", """
            CREATE TABLE conflict_keep_remote_members(
                original_id TEXT NOT NULL PRIMARY KEY REFERENCES journal_coalescing_members(operation_id),
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_preparations(command_id),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0),
                conflict_id TEXT NOT NULL UNIQUE REFERENCES upload_conflicts(conflict_id),
                UNIQUE(command_id,ordinal))
            """),
        ("index", "conflict_keep_remote_members_command", "CREATE INDEX conflict_keep_remote_members_command ON conflict_keep_remote_members(command_id,ordinal)"),
        ("table", "conflict_keep_remote_copies", """
            CREATE TABLE conflict_keep_remote_copies(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_preparations(command_id),
                side INTEGER NOT NULL CHECK(side IN (0,1)),
                payload BLOB NOT NULL CHECK(length(payload)<=65536),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(command_id,side))
            """)
    ];

    private static async Task EnsureConflictKeepRemoteSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in ConflictKeepRemoteSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 94) throw new InvalidDataException("Schema94 lost original KeepRemote preservation ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another preservation schema definition.");
        }
        if (originalVersion < 94)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM conflict_keep_remote_preparations UNION ALL SELECT 1 FROM conflict_keep_remote_members UNION ALL SELECT 1 FROM conflict_keep_remote_copies LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) throw new InvalidDataException("An older catalog cannot invent a KeepRemote preservation history.");
            query.CommandText = "PRAGMA user_version=94;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
