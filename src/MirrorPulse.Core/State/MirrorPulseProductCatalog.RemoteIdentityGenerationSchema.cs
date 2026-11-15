using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string RemoteIdentityGenerationHistoryWithDirectoryTransferSql = """
        CREATE TABLE remote_identity_generation_history(
            generation TEXT PRIMARY KEY,root_id TEXT NOT NULL,base_item_id TEXT NOT NULL,item_id TEXT NOT NULL UNIQUE,
            retirement_id TEXT UNIQUE REFERENCES namespace_member_retirements(operation_id),
            directory_retirement_id TEXT UNIQUE REFERENCES namespace_directory_transfer_retirements(effect_id),
            payload BLOB NOT NULL CHECK(length(payload)<=131072),fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
            CHECK((retirement_id IS NULL) <> (directory_retirement_id IS NULL)))
        """;

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
            bool history = name == "remote_identity_generation_history";
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name;";
            schema.Parameters.Clear(); schema.Parameters.AddWithValue("$name", name);
            object? existing = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 86) throw new InvalidDataException("Schema86 lost its durable remote identity generations.");
                schema.CommandText = sql; schema.Parameters.Clear(); await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else
            {
                string actual = NormalizeRetiredTransferIndexSql((string)existing);
                bool legacy = actual == NormalizeRetiredTransferIndexSql(sql);
                bool current = history && actual == NormalizeRetiredTransferIndexSql(RemoteIdentityGenerationHistoryWithDirectoryTransferSql);
                if (!(current || legacy && (!history || originalVersion < 116)))
                    throw new InvalidDataException("The remote identity schema lost its immutable history or bounded current lookup.");
            }
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

    private static async Task UpgradeDirectoryTransferIdentityGenerationSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        if (originalVersion >= 116) return;
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='remote_identity_generation_history';";
        if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is string existing &&
            NormalizeRetiredTransferIndexSql(existing) == NormalizeRetiredTransferIndexSql(RemoteIdentityGenerationHistoryWithDirectoryTransferSql))
        {
            schema.CommandText = "SELECT 1 FROM remote_identity_generation_history WHERE directory_retirement_id IS NOT NULL LIMIT 1;";
            if (await schema.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot supply directory transfer generation ownership.");
            schema.CommandText = "PRAGMA user_version=116;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
            return;
        }
        schema.CommandText = """
            ALTER TABLE remote_identity_generations RENAME TO previous_remote_identity_generations;
            ALTER TABLE remote_identity_generation_history RENAME TO previous_remote_identity_generation_history;
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        schema.CommandText = RemoteIdentityGenerationHistoryWithDirectoryTransferSql;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        schema.CommandText = RemoteIdentityGenerationSchema[1].Sql;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        schema.CommandText = """
            INSERT INTO remote_identity_generation_history(generation,root_id,base_item_id,item_id,retirement_id,payload,fingerprint)
            SELECT generation,root_id,base_item_id,item_id,retirement_id,payload,fingerprint FROM previous_remote_identity_generation_history;
            INSERT INTO remote_identity_generations(root_id,base_item_id,generation)
            SELECT root_id,base_item_id,generation FROM previous_remote_identity_generations;
            DROP TABLE previous_remote_identity_generations;
            DROP TABLE previous_remote_identity_generation_history;
            PRAGMA user_version=116;
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
