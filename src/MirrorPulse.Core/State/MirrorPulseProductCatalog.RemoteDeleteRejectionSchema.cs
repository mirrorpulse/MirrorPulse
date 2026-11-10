using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] RemoteDeleteRejectionSchema =
    [
        ("table", "remote_delete_rejections", """
            CREATE TABLE remote_delete_rejections(
                operation_id TEXT NOT NULL PRIMARY KEY,
                instance_id TEXT NOT NULL,root_id TEXT NOT NULL,batch_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal>=0),change_id TEXT NOT NULL,relative_path TEXT NOT NULL,
                admission_payload BLOB NOT NULL CHECK(length(admission_payload)<=1048576),
                admission_fingerprint BLOB NOT NULL CHECK(length(admission_fingerprint)=32),
                conflict_id TEXT NULL UNIQUE,
                outcome_payload BLOB CHECK(length(outcome_payload)<=4096),
                outcome_fingerprint BLOB CHECK(length(outcome_fingerprint)=32),
                CHECK((conflict_id IS NULL)=(outcome_payload IS NULL)),
                CHECK((outcome_payload IS NULL)=(outcome_fingerprint IS NULL)),
                UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id))
            """),
        ("index", "ix_remote_delete_rejections_pending_root", "CREATE INDEX ix_remote_delete_rejections_pending_root ON remote_delete_rejections(root_id,operation_id) WHERE outcome_payload IS NULL"),
        ("index", "ix_remote_delete_rejections_pending_change", "CREATE INDEX ix_remote_delete_rejections_pending_change ON remote_delete_rejections(instance_id,change_id,relative_path) WHERE outcome_payload IS NULL")
    ];

    private static async Task EnsureRemoteDeleteRejectionSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in RemoteDeleteRejectionSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 103) throw new InvalidDataException("Schema103 lost its original preserving Delete ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("Preserving Delete ownership cannot adopt another schema definition.");
        }
        if (originalVersion < 103)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM remote_delete_rejections LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent original non-mutating Delete receipts.");
            query.CommandText = "PRAGMA user_version=103;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
