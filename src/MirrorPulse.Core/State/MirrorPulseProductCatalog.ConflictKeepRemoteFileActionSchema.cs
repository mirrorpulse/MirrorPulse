using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] KeepRemoteFileActionSchema =
    [
        ("conflict_keep_remote_file_actions", """
            CREATE TABLE conflict_keep_remote_file_actions(
                command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_file_starts(command_id),
                action_id TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL CHECK(length(payload)<=262144),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("conflict_keep_remote_file_action_owners", """
            CREATE TABLE conflict_keep_remote_file_action_owners(
                command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_file_starts(command_id),
                action_id TEXT NOT NULL UNIQUE REFERENCES conflict_keep_remote_file_actions(action_id))
            """),
    ];

    private static async Task EnsureKeepRemoteFileActionSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in KeepRemoteFileActionSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 97) throw new InvalidDataException("Schema97 lost its original KeepRemote permission action ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another permission action schema definition.");
        }
        if (originalVersion < 97)
        {
            query.Parameters.Clear();
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_file_actions UNION ALL SELECT 1 FROM conflict_keep_remote_file_action_owners LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent KeepRemote permission action history.");
            query.CommandText = "PRAGMA user_version=97;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
