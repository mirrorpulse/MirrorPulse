using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] KeepRemoteFileContentSchema =
    [
        ("conflict_keep_remote_content_facts", """
            CREATE TABLE conflict_keep_remote_content_facts(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                payload BLOB NOT NULL CHECK(length(payload)<=262144),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(command_id,kind))
            """),
        ("conflict_keep_remote_content_owners", """
            CREATE TABLE conflict_keep_remote_content_owners(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                PRIMARY KEY(command_id,kind))
            """),
    ];

    private static async Task EnsureKeepRemoteFileContentSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in KeepRemoteFileContentSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 98) throw new InvalidDataException("Schema98 lost its original KeepRemote content ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another content execution schema.");
        }
        if (originalVersion < 98)
        {
            query.Parameters.Clear();
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_content_facts UNION ALL SELECT 1 FROM conflict_keep_remote_content_owners LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent KeepRemote content execution history.");
            query.CommandText = "PRAGMA user_version=98;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
