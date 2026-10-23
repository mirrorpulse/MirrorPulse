using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Name, string Sql)[] RemoteIdentityGenerationSchema =
    [
        ("remote_identity_generation_history", """
            CREATE TABLE remote_identity_generation_history(
                generation TEXT PRIMARY KEY,root_id TEXT NOT NULL,base_item_id TEXT NOT NULL,item_id TEXT NOT NULL UNIQUE,
                retirement_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_retirements(operation_id),
                payload BLOB NOT NULL CHECK(length(payload)<=131072),fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("remote_identity_generations", """
            CREATE TABLE remote_identity_generations(root_id TEXT NOT NULL,base_item_id TEXT NOT NULL,
                generation TEXT NOT NULL UNIQUE REFERENCES remote_identity_generation_history(generation),PRIMARY KEY(root_id,base_item_id))
            """)
    ];

    private static async Task EnsureRemoteIdentityGenerationSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        foreach (var (name, sql) in RemoteIdentityGenerationSchema)
        {
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            schema.Parameters.Clear(); schema.Parameters.AddWithValue("$name", name);
            object? existing = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 86) throw new InvalidDataException("Schema86 lost its durable remote identity generations.");
                schema.CommandText = sql; schema.Parameters.Clear(); await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (NormalizeRetiredTransferIndexSql((string)existing) != NormalizeRetiredTransferIndexSql(sql))
                throw new InvalidDataException("The remote identity schema lost its immutable history or bounded current lookup.");
            if (originalVersion < 86)
            {
                schema.Parameters.Clear(); schema.CommandText = "SELECT 1 FROM " + name + " LIMIT 1;";
                if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("An older catalog cannot invent successor generation ownership.");
            }
        }
        if (originalVersion < 86)
        {
            schema.Parameters.Clear(); schema.CommandText = "PRAGMA user_version=86;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
