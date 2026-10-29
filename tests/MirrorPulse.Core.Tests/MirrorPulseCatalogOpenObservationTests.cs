using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MirrorPulseCatalogOpenObservationTests
{
    [TestMethod]
    public async Task ActualFreshAndReopenedCatalogEmitOnlyClosedOrderedAnonymousTimingTuples()
    {
        using var fixture = new Fixture(); using var observer = new Observer();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync()).Roots);
        }
        string[] phases = Enum.GetNames<MirrorPulseCatalogOpenPhase>();
        Assert.HasCount(2 * phases.Length, observer.Events);
        var traces = observer.Events.GroupBy(item => ((ValueTuple<Guid, long>)item.Value!).Item1).ToArray();
        Assert.HasCount(2, traces);
        foreach (var trace in traces)
        {
            Assert.AreNotEqual(Guid.Empty, trace.Key);
            CollectionAssert.AreEqual(phases, trace.Select(item => item.Key).ToArray());
            long previous = -1;
            foreach (var item in trace)
            {
                Assert.IsInstanceOfType<ValueTuple<Guid, long>>(item.Value);
                long elapsed = ((ValueTuple<Guid, long>)item.Value!).Item2;
                Assert.IsGreaterThanOrEqualTo(previous, elapsed); previous = elapsed;
            }
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath + ".owner"));
    }

    [TestMethod]
    [DataRow("predicate")]
    [DataRow("delivery")]
    public async Task ObserverFailuresCannotReplaceSuccessfulInitializationOrLeakItsCatalogOwner(string failure)
    {
        using var fixture = new Fixture(); using var observer = new Observer(failure);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync()).Roots);
        Assert.IsGreaterThan(0, observer.Failures);
        Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath + ".owner"));
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsEmpty((await reopened.ReadAdapterTopologyAsync()).Roots);
    }

    [TestMethod]
    [DataRow("predicate")]
    [DataRow("delivery")]
    public async Task ObserverFailuresPreserveTheOriginalSqliteFailureAndAtomicBaseSchema(string failure)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.ProductCatalogDatabasePath)!);
        string connectionString = new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString();
        await using (var seed = new SqliteConnection(connectionString))
        {
            await seed.OpenAsync(); await using var command = seed.CreateCommand();
            command.CommandText = "CREATE TABLE managed_root_renames (root_id TEXT PRIMARY KEY,sentinel TEXT NOT NULL); INSERT INTO managed_root_renames VALUES ('original','retained');";
            await command.ExecuteNonQueryAsync();
        }
        var original = await Assert.ThrowsExactlyAsync<SqliteException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        using var observer = new Observer(failure);
        var observed = await Assert.ThrowsExactlyAsync<SqliteException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        Assert.AreEqual(original.HResult, observed.HResult); Assert.AreEqual(original.SqliteErrorCode, observed.SqliteErrorCode);
        Assert.AreEqual(original.SqliteExtendedErrorCode, observed.SqliteExtendedErrorCode); Assert.AreEqual(original.Message, observed.Message);
        Assert.IsGreaterThan(0, observer.Failures);
        Assert.IsFalse(observer.Events.Any(item => item.Key == nameof(MirrorPulseCatalogOpenPhase.Completed)));
        await using var verify = new SqliteConnection(connectionString); await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        Assert.AreEqual(1L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT sentinel FROM managed_root_renames WHERE root_id='original';"; Assert.AreEqual("retained", await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(0L, await query.ExecuteScalarAsync());
    }

    [TestMethod]
    [DataRow("predicate")]
    [DataRow("delivery")]
    public async Task ObserverFailuresCannotReplaceTheOriginalCancellationToken(string failure)
    {
        using var fixture = new Fixture(); using var observer = new Observer(failure);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        OperationCanceledException? actual = null;
        try { await using var unexpected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, stop.Token); Assert.Fail("The cancelled catalog opened."); }
        catch (OperationCanceledException error) { actual = error; }
        Assert.IsNotNull(actual); Assert.AreEqual(stop.Token, actual.CancellationToken); Assert.IsGreaterThan(0, observer.Failures);
        Assert.IsFalse(observer.Events.Any(item => item.Key == nameof(MirrorPulseCatalogOpenPhase.Completed)));
        Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath + ".owner"));
    }

    [TestMethod]
    public void NumericOrUnknownPhaseValuesCannotBecomeDiagnosticNames()
    {
        using var observer = new Observer(); var observation = MirrorPulseCatalogOpenObservation.Begin();
        observation.Mark((MirrorPulseCatalogOpenPhase)999); observation.Mark((MirrorPulseCatalogOpenPhase)(-1));
        observation.Mark(MirrorPulseCatalogOpenPhase.Completed);
        Assert.HasCount(1, observer.Events); Assert.AreEqual(nameof(MirrorPulseCatalogOpenPhase.Completed), observer.Events[0].Key);
        Assert.IsInstanceOfType<ValueTuple<Guid, long>>(observer.Events[0].Value);
    }

    private sealed class Observer : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string? _failure;
        private readonly List<IDisposable> _events = [];
        private readonly IDisposable _sources;
        internal List<KeyValuePair<string, object?>> Events { get; } = [];
        internal int Failures { get; private set; }

        internal Observer(string? failure = null)
        {
            _failure = failure; _sources = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name != MirrorPulseCatalogOpenObservation.SourceName) return;
            _events.Add(_failure == "predicate" ? value.Subscribe(this, _ =>
            { Failures++; throw new InvalidOperationException("private observer predicate"); }) : value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (_failure == "delivery") { Failures++; throw new InvalidOperationException("private observer delivery"); }
            Events.Add(value);
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose()
        {
            _sources.Dispose(); foreach (var subscription in _events) subscription.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-catalog-observation-" + Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths => new(Path.Combine(_directory, "sync-unused"), Path.Combine(_directory, "private"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
