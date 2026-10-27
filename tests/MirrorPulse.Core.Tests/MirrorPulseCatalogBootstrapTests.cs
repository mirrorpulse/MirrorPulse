using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseCatalogBootstrapTests
{
    [TestMethod]
    [DataRow("managed_root_renames", "root_id")]
    [DataRow("namespace_birth_protections", "protection_id")]
    public async Task FailedBaseSchemaPreservesOriginalTablesAndRowsWithoutPartialBootstrap(string table, string key)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-catalog-bootstrap-" + Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ProductCatalogDatabasePath,
            Pooling = false,
        }.ToString();
        try
        {
            await using (var original = new SqliteConnection(connectionString))
            {
                await original.OpenAsync();
                await using var seed = original.CreateCommand();
                seed.CommandText = $"CREATE TABLE {table} ({key} TEXT PRIMARY KEY, sentinel TEXT NOT NULL); INSERT INTO {table} VALUES ('original', 'retained');";
                await seed.ExecuteNonQueryAsync();
            }

            // An incompatible pre-existing table fails inside the real base DDL batch.
            // Failure must not leave unrelated newly created tables behind.
            await Assert.ThrowsExactlyAsync<SqliteException>(() => MirrorPulseProductCatalog.OpenAsync(paths));

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync();
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            Assert.AreEqual(1L, await count.ExecuteScalarAsync());
            await using var row = verify.CreateCommand();
            row.CommandText = $"SELECT sentinel FROM {table} WHERE {key} = 'original';";
            Assert.AreEqual("retained", await row.ExecuteScalarAsync());
            await using var version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(0L, await version.ExecuteScalarAsync());
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath));
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
