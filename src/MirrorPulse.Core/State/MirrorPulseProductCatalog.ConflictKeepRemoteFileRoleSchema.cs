using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] KeepRemoteFileRoleSchema =
    [
        ("conflict_keep_remote_role_facts", """
            CREATE TABLE conflict_keep_remote_role_facts(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 512),
                recovery_id TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                payload BLOB NOT NULL CHECK(length(payload)<=262144),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(command_id,ordinal), UNIQUE(command_id,recovery_id,kind))
            """),
        ("conflict_keep_remote_role_owners", """
            CREATE TABLE conflict_keep_remote_role_owners(
                command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 512),
                recovery_id TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind IN (1,2)),
                action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
                PRIMARY KEY(command_id,ordinal), UNIQUE(command_id,recovery_id,kind))
            """),
    ];

    private static async Task EnsureKeepRemoteFileRoleSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        foreach (var definition in KeepRemoteFileRoleSchema)
        {
            query.Parameters.Clear();
            query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 101) throw new InvalidDataException("Schema101 lost its original KeepRemote role ownership.");
                query.Parameters.Clear();
                query.CommandText = definition.Sql;
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another role recovery schema.");
        }
        if (originalVersion < 101)
        {
            query.Parameters.Clear();
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_role_facts UNION ALL SELECT 1 FROM conflict_keep_remote_role_owners LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent KeepRemote role history.");
            query.CommandText = "PRAGMA user_version=101;";
            await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
