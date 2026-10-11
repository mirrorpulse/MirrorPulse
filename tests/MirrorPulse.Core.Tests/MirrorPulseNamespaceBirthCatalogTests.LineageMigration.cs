using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task Schema31MigrationPreservesOriginalBytesBindingsEpochsAndReservations()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        BornObject parent;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, permission);
            parent = await AddBornAsync(first, Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" });
            var nextEpoch = parent.Protection with
            {
                ProtectionId = Guid.NewGuid(),
                PreviousProtectionId = parent.Protection.ProtectionId,
                Verification = parent.Protection.Verification with { ObservedAt = parent.Protection.Verification.ObservedAt.AddSeconds(1) }
            };
            await first.RecordNamespaceBirthProtectionAsync(nextEpoch);
            parent = parent with { Protection = nextEpoch };
        }
        await MakeSchema31Async(fixture.Paths);
        string[] before = await ReadBirthArtifactBytesAsync(fixture.Paths);
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(parent.Birth, await second.PrepareNamespaceBirthAsync(parent.Birth));
            Assert.AreEqual(parent.Observation, await second.ReadNamespaceBirthObservationAsync(parent.Birth.OperationId));
            Assert.AreEqual(parent.Protection, await second.ReadLatestNamespaceBirthProtectionAsync(parent.Birth.OperationId));
            Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
            var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
            var entry = (await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1)).Entries.Single();
            Assert.IsTrue(entry.OwnsNameReservation);
            Assert.IsNull(entry.Intent.BornParent);
        }
        CollectionAssert.AreEqual(before, await ReadBirthArtifactBytesAsync(fixture.Paths));
        await using (var queryConnection = Connection(fixture.Paths))
        {
            await queryConnection.OpenAsync();
            await using var version = queryConnection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(32L, await version.ExecuteScalarAsync());
            version.CommandText = "PRAGMA foreign_key_check;";
            Assert.IsNull(await version.ExecuteScalarAsync());
        }
        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(parent.Protection, await third.ReadLatestNamespaceBirthProtectionAsync(parent.Birth.OperationId));
        var child = NestedBirth(parent, "child.txt", false);
        Assert.AreEqual(child, await third.PrepareNamespaceBirthAsync(child));
        var originalSibling = Birth(original, permission) with { RelativePath = "Docs/B" };
        Assert.AreEqual(originalSibling, await third.PrepareNamespaceBirthAsync(originalSibling));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task Schema31MigrationRollsBackInsteadOfInventingMissingOriginalReferences()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, permission);
            await AddBornAsync(first, Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" });
        }
        await MakeSchema31Async(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM namespace_permission_changes;");
        string[] before = await ReadBirthArtifactBytesAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        CollectionAssert.AreEqual(before, await ReadBirthArtifactBytesAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(31L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT 1 FROM pragma_table_info('namespace_birth_intents') WHERE name='parent_birth_operation_id';";
        Assert.IsNull(await query.ExecuteScalarAsync());
        query.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%_parent_migration';";
        Assert.AreEqual(0L, await query.ExecuteScalarAsync());
    }

    private static async Task<string[]> ReadBirthArtifactBytesAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        var result = new List<string>();
        foreach (string table in new[] { "namespace_birth_intents", "namespace_birth_plans", "namespace_birth_starts",
            "namespace_birth_conversions", "namespace_birth_observations", "namespace_birth_protections" })
        {
            await using var query = connection.CreateCommand();
            query.CommandText = $"SELECT rowid,hex(payload),hex(fingerprint) FROM {table} ORDER BY rowid;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add($"{table}:{reader.GetInt64(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
        }
        return result.ToArray();
    }

    private static Task MakeSchema31Async(MirrorPulseStoragePaths paths) => ExecuteSqlAsync(paths, """
        PRAGMA foreign_keys=OFF;
        BEGIN;
        CREATE TABLE legacy_birth_intents (
            operation_id TEXT PRIMARY KEY,
            parent_evidence_id TEXT NOT NULL REFERENCES namespace_permission_baselines(evidence_id),
            parent_operation_id TEXT NOT NULL REFERENCES namespace_permission_changes(operation_id),
            relative_path_key TEXT NOT NULL, payload BLOB NOT NULL,
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
        );
        INSERT INTO legacy_birth_intents(rowid,operation_id,parent_evidence_id,parent_operation_id,relative_path_key,payload,fingerprint)
            SELECT rowid,operation_id,parent_evidence_id,parent_operation_id,relative_path_key,payload,fingerprint FROM namespace_birth_intents;
        CREATE TABLE legacy_birth_reservations (
            parent_evidence_id TEXT NOT NULL REFERENCES namespace_permission_baselines(evidence_id),
            child_name_key TEXT NOT NULL,
            operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_intents(operation_id),
            PRIMARY KEY(parent_evidence_id,child_name_key)
        );
        INSERT INTO legacy_birth_reservations SELECT parent_id,child_name_key,operation_id FROM namespace_birth_reservations;
        CREATE TABLE legacy_birth_protections (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            protection_id TEXT NOT NULL UNIQUE,
            birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_observations(operation_id),
            previous_protection_id TEXT NULL UNIQUE REFERENCES namespace_birth_protections(protection_id),
            parent_permission_operation_id TEXT NOT NULL REFERENCES namespace_permission_changes(operation_id),
            payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
        );
        INSERT INTO legacy_birth_protections(sequence,protection_id,birth_operation_id,previous_protection_id,parent_permission_operation_id,payload,fingerprint)
            SELECT sequence,protection_id,birth_operation_id,previous_protection_id,parent_permission_operation_id,payload,fingerprint FROM namespace_birth_protections;
        DROP TABLE namespace_birth_reservations;
        DROP TABLE namespace_birth_intents;
        DROP TABLE namespace_birth_protections;
        ALTER TABLE legacy_birth_intents RENAME TO namespace_birth_intents;
        ALTER TABLE legacy_birth_reservations RENAME TO namespace_birth_reservations;
        ALTER TABLE legacy_birth_protections RENAME TO namespace_birth_protections;
        CREATE INDEX namespace_birth_latest_protection ON namespace_birth_protections(birth_operation_id,sequence DESC);
        PRAGMA user_version=31;
        COMMIT;
        PRAGMA foreign_keys=ON;
        """);
}
