using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] ConflictRemoteObservationSchema =
    [
        ("table", "coalesced_conflict_remote_observations", """
            CREATE TABLE coalesced_conflict_remote_observations(
                observation_id TEXT NOT NULL PRIMARY KEY,
                rejected_operation_id TEXT NOT NULL REFERENCES mutation_intents(operation_id),
                plan_id TEXT NOT NULL REFERENCES journal_coalescing_plans(plan_id),
                refresh_command_id TEXT NULL UNIQUE REFERENCES user_commands(command_id),
                payload BLOB NOT NULL CHECK(length(payload)<=16777216),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("index", "coalesced_conflict_remote_observations_effect", "CREATE INDEX coalesced_conflict_remote_observations_effect ON coalesced_conflict_remote_observations(rejected_operation_id,observation_id)"),
        ("table", "coalesced_conflict_remote_heads", """
            CREATE TABLE coalesced_conflict_remote_heads(
                rejected_operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
                observation_id TEXT NOT NULL UNIQUE REFERENCES coalesced_conflict_remote_observations(observation_id))
            """),
        ("table", "coalesced_conflict_remote_members", """
            CREATE TABLE coalesced_conflict_remote_members(
                selected_conflict_id TEXT NOT NULL PRIMARY KEY REFERENCES upload_conflicts(conflict_id),
                previous_conflict_id TEXT NOT NULL UNIQUE REFERENCES upload_conflicts(conflict_id),
                observation_id TEXT NOT NULL REFERENCES coalesced_conflict_remote_observations(observation_id),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0),
                original_id TEXT NOT NULL REFERENCES journal_coalescing_members(operation_id),
                UNIQUE(observation_id,ordinal),UNIQUE(observation_id,original_id))
            """)
    ];

    private static async Task EnsureConflictRemoteObservationSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in ConflictRemoteObservationSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 93) throw new InvalidDataException("Schema93 lost original remote observation ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql;
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("Remote observation ownership cannot adopt another schema definition.");
        }
        if (originalVersion < 93)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM coalesced_conflict_remote_observations UNION ALL SELECT 1 FROM coalesced_conflict_remote_members UNION ALL SELECT 1 FROM coalesced_conflict_remote_heads LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent post-rejection version observations.");
            query.CommandText = "PRAGMA user_version=93;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
