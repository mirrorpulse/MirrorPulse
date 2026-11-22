using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static async Task EnsureNamespaceBirthParentSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using (SqliteCommand columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT 1 FROM pragma_table_info('namespace_birth_intents') WHERE name='parent_birth_operation_id';";
            if (await columns.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) return;
        }
        // The catalog's exclusive owner is already held. SQLite requires foreign keys to be
        // disabled outside this rebuild transaction. Copy original bytes and rowids verbatim;
        // never rename the old parent first, which would rewrite the dependent FK targets.
        await using (SqliteCommand disable = connection.CreateCommand())
        {
            disable.CommandText = "PRAGMA foreign_keys=OFF;";
            await disable.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            await using (SqliteCommand migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = """
                    CREATE TABLE namespace_birth_intents_parent_migration (
                        operation_id TEXT PRIMARY KEY,
                        parent_evidence_id TEXT NULL REFERENCES namespace_permission_baselines(evidence_id),
                        parent_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
                        parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
                        parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
                        relative_path_key TEXT NOT NULL,
                        payload BLOB NOT NULL,
                        fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                        CHECK((parent_evidence_id IS NOT NULL AND parent_operation_id IS NOT NULL AND
                            parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL) OR
                            (parent_evidence_id IS NULL AND parent_operation_id IS NULL AND
                            parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
                    );
                    INSERT INTO namespace_birth_intents_parent_migration(rowid,operation_id,parent_evidence_id,parent_operation_id,
                        relative_path_key,payload,fingerprint)
                    SELECT rowid,operation_id,parent_evidence_id,parent_operation_id,relative_path_key,payload,fingerprint
                        FROM namespace_birth_intents;
                    CREATE TABLE namespace_birth_reservations_parent_migration (
                        parent_kind INTEGER NOT NULL CHECK(parent_kind IN (0,1)),
                        parent_id TEXT NOT NULL,
                        child_name_key TEXT NOT NULL,
                        operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_intents(operation_id),
                        PRIMARY KEY(parent_kind,parent_id,child_name_key)
                    );
                    INSERT INTO namespace_birth_reservations_parent_migration(parent_kind,parent_id,child_name_key,operation_id)
                    SELECT 0,parent_evidence_id,child_name_key,operation_id FROM namespace_birth_reservations;
                    CREATE TABLE namespace_birth_protections_parent_migration (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        protection_id TEXT NOT NULL UNIQUE,
                        birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_observations(operation_id),
                        previous_protection_id TEXT NULL UNIQUE REFERENCES namespace_birth_protections(protection_id),
                        parent_permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
                        parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
                        parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
                        payload BLOB NOT NULL,
                        fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                        CHECK((parent_permission_operation_id IS NOT NULL AND parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL) OR
                            (parent_permission_operation_id IS NULL AND parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
                    );
                    INSERT INTO namespace_birth_protections_parent_migration(sequence,protection_id,birth_operation_id,
                        previous_protection_id,parent_permission_operation_id,payload,fingerprint)
                    SELECT sequence,protection_id,birth_operation_id,previous_protection_id,parent_permission_operation_id,payload,fingerprint
                        FROM namespace_birth_protections;
                    DROP TABLE namespace_birth_reservations;
                    DROP TABLE namespace_birth_intents;
                    DROP TABLE namespace_birth_protections;
                    ALTER TABLE namespace_birth_intents_parent_migration RENAME TO namespace_birth_intents;
                    ALTER TABLE namespace_birth_reservations_parent_migration RENAME TO namespace_birth_reservations;
                    ALTER TABLE namespace_birth_protections_parent_migration RENAME TO namespace_birth_protections;
                    CREATE INDEX namespace_birth_latest_protection ON namespace_birth_protections(birth_operation_id,sequence DESC);
                    """;
                await migrate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (SqliteCommand check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "PRAGMA foreign_key_check;";
                await using SqliteDataReader reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw new InvalidDataException("The namespace birth migration lost an original foreign-key reference.");
            }
            transaction.Commit();
        }
        finally
        {
            await using SqliteCommand enable = connection.CreateCommand();
            enable.CommandText = "PRAGMA foreign_keys=ON;";
            await enable.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await using SqliteCommand enforcement = connection.CreateCommand();
        enforcement.CommandText = "PRAGMA foreign_keys;";
        if ((long)(await enforcement.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L) != 1)
            throw new InvalidDataException("The namespace birth migration could not restore foreign-key enforcement.");
    }
}
