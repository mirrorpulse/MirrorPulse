using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] KeepRemoteFileNativeSchema =
    [
        ("conflict_keep_remote_native_facts", """
            CREATE TABLE conflict_keep_remote_native_facts(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 512),
                attempt_id TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                payload BLOB NOT NULL CHECK(length(payload)<=262144),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(command_id,ordinal), UNIQUE(command_id,attempt_id,kind))
            """),
        ("conflict_keep_remote_native_owners", """
            CREATE TABLE conflict_keep_remote_native_owners(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 512),
                attempt_id TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                PRIMARY KEY(command_id,ordinal), UNIQUE(command_id,attempt_id,kind))
            """),
    ];

    private static async Task EnsureKeepRemoteFileNativeSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in KeepRemoteFileNativeSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 99) throw new InvalidDataException("Schema99 lost its original KeepRemote native ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another native execution schema.");
        }
        if (originalVersion < 99)
        {
            query.Parameters.Clear();
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_native_facts UNION ALL SELECT 1 FROM conflict_keep_remote_native_owners LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent KeepRemote native execution history.");
            query.CommandText = "PRAGMA user_version=99;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
