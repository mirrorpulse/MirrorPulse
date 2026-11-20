using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MirrorPulseCatalogFreshSchemaTests
{
    [TestMethod]
    public async Task NewlyCreatedCatalogHasEveryOriginalEmptyFileSchemaDefinitionAndNoPreparedRows()
    {
        using var fixture = new Fixture(); var fresh = fixture.Paths("new"); var existing = fixture.Paths("existing-empty");
        Directory.CreateDirectory(Path.GetDirectoryName(existing.ProductCatalogDatabasePath)!);
        await File.WriteAllBytesAsync(existing.ProductCatalogDatabasePath, []);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fresh)) { }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(existing)) { }
        var actual = await ReadSchemaAsync(fresh); var original = await ReadSchemaAsync(existing);
        CollectionAssert.AreEqual(original.Definitions, actual.Definitions);
        Assert.AreEqual(original.Version, actual.Version);
        Assert.HasCount(original.Rows.Count, actual.Rows);
        foreach (var table in actual.Rows) { Assert.AreEqual(0L, table.Value); Assert.AreEqual(original.Rows[table.Key], table.Value); }
        Assert.IsFalse(File.Exists(fresh.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fresh.SyncRootPath));
    }

    [TestMethod]
    public async Task ExistingCatalogRetainsItsUserCommandInsteadOfReapplyingTheFreshSchema()
    {
        using var fixture = new Fixture(); var paths = fixture.Paths("history"); Guid command = Guid.NewGuid(); string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
        {
            await catalog.SaveUserCommandAsync(new(command, "status", "retained", "pending"));
            original = JsonSerializer.Serialize(await catalog.ReadUserCommandAsync(command));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
        Assert.AreEqual(original, JsonSerializer.Serialize(await reopened.ReadUserCommandAsync(command)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ANewFileThatAcquiresForeignSchemaOrVersionIsRefusedWithoutChangingIt(bool setVersion)
    {
        using var fixture = new Fixture(); var paths = fixture.Paths("raced");
        using var observer = new BeforeSchema(() =>
        {
            using var connection = Connection(paths); connection.Open(); using var query = connection.CreateCommand();
            query.CommandText = setVersion ? "PRAGMA user_version=1;" : "CREATE TABLE retained_external(payload TEXT NOT NULL); INSERT INTO retained_external VALUES('original');";
            query.ExecuteNonQuery();
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
        Assert.IsTrue(observer.Invoked); Assert.IsNull(observer.Failure);
        await using var verify = Connection(paths); await verify.OpenAsync(); await using var query = verify.CreateCommand();
        if (setVersion) { query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(1L, await query.ExecuteScalarAsync()); }
        else
        {
            query.CommandText = "SELECT payload FROM retained_external;"; Assert.AreEqual("original", await query.ExecuteScalarAsync());
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';"; Assert.AreEqual(1L, await query.ExecuteScalarAsync());
        }
        Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath + ".owner"));
    }

    [TestMethod]
    public async Task CancellationBeforeFreshDdlKeepsTheFileWithoutPartialSchemaOrVersion()
    {
        using var fixture = new Fixture(); var paths = fixture.Paths("cancelled"); using var stop = new CancellationTokenSource();
        using var observer = new BeforeSchema(stop.Cancel);
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseProductCatalog.OpenAsync(paths, stop.Token));
        Assert.AreEqual(stop.Token, failure.CancellationToken); Assert.IsTrue(observer.Invoked);
        await using var verify = Connection(paths); await verify.OpenAsync(); await using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master;"; Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath + ".owner"));
    }

    [TestMethod]
    public async Task FreshCatalogKeepsWalFullSynchronizationAndItsCompleteForeignKeyGraph()
    {
        using var fixture = new Fixture(); var paths = fixture.Paths("policies");
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        await using var verify = Connection(paths); await verify.OpenAsync(); await using var query = verify.CreateCommand();
        query.CommandText = "PRAGMA journal_mode;"; Assert.AreEqual("wal", await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA synchronous;"; Assert.AreEqual(2L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA foreign_key_check;"; await using var rows = await query.ExecuteReaderAsync(); Assert.IsFalse(await rows.ReadAsync());
    }

    private static async Task<(string[] Definitions, Dictionary<string, long> Rows, long Version)> ReadSchemaAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); var definitions = new List<string>(); var tables = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name;";
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                definitions.Add(reader.GetString(0) + "|" + reader.GetString(1) + "|" + reader.GetString(2) + "|" + reader.GetString(3));
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            if (table.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) throw new InvalidDataException("Unexpected product schema name.");
            await using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM \"" + table + "\";";
            counts.Add(table, (long)(await query.ExecuteScalarAsync())!);
        }
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        return (definitions.ToArray(), counts, (long)(await version.ExecuteScalarAsync())!);
    }

    private static SqliteConnection Connection(MirrorPulseStoragePaths paths) => new(new SqliteConnectionStringBuilder
    { DataSource = paths.ProductCatalogDatabasePath, Pooling = false, ForeignKeys = true }.ToString());

    private sealed class BeforeSchema : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly Action _action; private readonly IDisposable _sources; private readonly List<IDisposable> _events = [];
        internal bool Invoked { get; private set; }
        internal Exception? Failure { get; private set; }
        internal BeforeSchema(Action action) { _action = action; _sources = DiagnosticListener.AllListeners.Subscribe(this); }
        public void OnNext(DiagnosticListener value)
        { if (value.Name == MirrorPulseCatalogOpenObservation.SourceName) _events.Add(value.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Invoked || value.Key != nameof(MirrorPulseCatalogOpenPhase.BaseSchemaCreating)) return;
            Invoked = true; try { _action(); } catch (Exception failure) { Failure = failure; }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { _sources.Dispose(); foreach (var subscription in _events) subscription.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-catalog-fresh-" + Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths(string name) => new(Path.Combine(_directory, name, "sync-unused"), Path.Combine(_directory, name, "private"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
