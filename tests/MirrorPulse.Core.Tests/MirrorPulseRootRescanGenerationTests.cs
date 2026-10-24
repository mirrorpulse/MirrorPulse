using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootRescanGenerationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OldDiscoveryCannotCompleteNewRequestsDuringScanOrAcknowledgement(bool acknowledged)
    {
        using var fixture = new Fixture();
        RootId changed = new(Guid.NewGuid()), completed = new(Guid.NewGuid()), added = new(Guid.NewGuid());
        MirrorPulseRootRescanRequest original, newer, other;
        Guid generation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await catalog.RequireRootRescanAsync(changed);
            other = await catalog.RequireRootRescanAsync(completed);
            generation = (await catalog.BeginFullRescanAsync()).Generation;
            await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Running);
            if (acknowledged) await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Acknowledging);
            newer = await catalog.RequireRootRescanAsync(changed);
            await catalog.RequireRootRescanAsync(added);
            Assert.AreNotEqual(original.Generation, newer.Generation);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        IReadOnlyList<MirrorPulseRootRescanRequest> captured = await reopened.ReadCapturedRootRescanRequestsAsync(generation);
        Assert.HasCount(2, captured);
        Assert.AreEqual(original, captured.Single(request => request.RootId == changed));
        Assert.IsFalse(await reopened.CompleteRootRescanAsync(changed, original.Generation));
        Assert.IsFalse(await reopened.CompleteRootRescanAsync(changed, other.Generation));
        Assert.IsTrue(await reopened.CompleteRootRescanAsync(completed, other.Generation));
        Assert.IsFalse(await reopened.CompleteRootRescanAsync(completed, other.Generation));
        Assert.AreEqual(newer, (await reopened.ReadRootRescanRequestsAsync()).Single(request => request.RootId == changed));
        Assert.HasCount(2, await reopened.ReadRootRescanRequestsAsync());
        if (!acknowledged) await reopened.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Acknowledging);
        await reopened.CompleteFullRescanAsync(generation);
        Assert.HasCount(2, await reopened.ReadRootRescanRequestsAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadCapturedRootRescanRequestsAsync(generation));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RestartRepeatsDiscoveryAndCapturesNewRequestsWithoutClearingOfflineRoots(int interruptionStage)
    {
        using var fixture = new Fixture();
        RootId active = new(Guid.NewGuid()), offline = new(Guid.NewGuid()), fenced = new(Guid.NewGuid());
        MirrorPulseRootRescanRequest? newer = null;
        Guid generation;
        int discoveries = 0;
        async ValueTask AddRequest(MirrorPulseProductCatalog catalog, CancellationToken token) => newer = await catalog.RequireRootRescanAsync(active, token);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.RequireRootRescanAsync(active);
            await catalog.RequireRootRescanAsync(offline);
            await catalog.RequireRootRescanAsync(fenced);
            var recovery = new MirrorPulseFullRescanRecovery(catalog, async token =>
            {
                discoveries++;
                await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "discovered"), "first", token);
                if (interruptionStage == 0) await AddRequest(catalog, token);
                return 1;
            }, async token => { if (interruptionStage == 1) await AddRequest(catalog, token); }, async (scan, token) =>
            {
                if (interruptionStage == 2) await AddRequest(catalog, token);
                MirrorPulseRootRescanRequest request = (await catalog.ReadCapturedRootRescanRequestsAsync(scan, token)).Single(request => request.RootId == active);
                Assert.IsFalse(await catalog.CompleteRootRescanAsync(active, request.Generation, token));
                throw new IOException("Projection interrupted after the stale completion attempt.");
            });
            await Assert.ThrowsExactlyAsync<IOException>(() => recovery.RunAsync(new(true)).AsTask());
            generation = (await catalog.ReadFullRescanAsync())!.Generation;
            Assert.AreEqual(MirrorPulseFullRescanPhase.Acknowledging, (await catalog.ReadFullRescanAsync())!.Phase);
            Assert.HasCount(3, await catalog.ReadRootRescanRequestsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var resumed = new MirrorPulseFullRescanRecovery(reopened, async token =>
        {
            discoveries++;
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "discovered"), "second", token);
            return 1;
        }, _ => ValueTask.CompletedTask, async (scan, token) =>
        {
            Assert.AreEqual(generation, scan);
            MirrorPulseRootRescanRequest request = (await reopened.ReadCapturedRootRescanRequestsAsync(scan, token)).Single(request => request.RootId == active);
            Assert.AreEqual(newer, request);
            Assert.IsTrue(await reopened.CompleteRootRescanAsync(active, request.Generation, token));
        });
        Assert.IsTrue((await resumed.RunAsync(new(false))).WasRequired);
        Assert.AreEqual(2, discoveries);
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "discovered")));
        CollectionAssert.AreEquivalent(new[] { offline, fenced }, (await reopened.ReadDeferredRescanRootsAsync()).ToArray());
        Assert.IsNull(await reopened.ReadFullRescanAsync());
    }

    [TestMethod]
    [DataRow(MirrorPulseFullRescanPhase.Running)]
    [DataRow(MirrorPulseFullRescanPhase.Acknowledging)]
    public async Task LegacyMarkersBecomeNewObligationsAndNeverPretendToHaveBeenScanned(MirrorPulseFullRescanPhase phase)
    {
        using var fixture = new Fixture();
        RootId root = new(Guid.NewGuid());
        Guid generation, oldRequest;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            oldRequest = (await catalog.RequireRootRescanAsync(root)).Generation;
            generation = (await catalog.BeginFullRescanAsync()).Generation;
            await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Running);
            if (phase == MirrorPulseFullRescanPhase.Acknowledging) await catalog.UpdateFullRescanAsync(generation, phase);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE full_rescan_root_requests; DROP TABLE full_rescan_request_snapshots; DROP TABLE root_rescan_generations; PRAGMA user_version=47;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        MirrorPulseRootRescanRequest migrated = (await reopened.ReadRootRescanRequestsAsync()).Single();
        Assert.AreEqual(root, migrated.RootId);
        Assert.AreNotEqual(oldRequest, migrated.Generation);
        Assert.AreEqual(generation, (await reopened.ReadFullRescanAsync())!.Generation);
        Assert.AreEqual(phase, (await reopened.ReadFullRescanAsync())!.Phase);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadCapturedRootRescanRequestsAsync(generation));
        if (phase == MirrorPulseFullRescanPhase.Acknowledging)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.CompleteFullRescanAsync(generation));
        int discoveries = 0;
        var resumed = new MirrorPulseFullRescanRecovery(reopened, _ => { discoveries++; return ValueTask.FromResult(0); },
            _ => ValueTask.CompletedTask, async (scan, token) =>
            {
                Assert.AreEqual(migrated, (await reopened.ReadCapturedRootRescanRequestsAsync(scan, token)).Single());
                Assert.IsTrue(await reopened.CompleteRootRescanAsync(root, migrated.Generation, token));
            });
        await resumed.RunAsync(new(false));
        Assert.AreEqual(1, discoveries);
        Assert.IsEmpty(await reopened.ReadDeferredRescanRootsAsync());
        Assert.IsNull(await reopened.ReadFullRescanAsync());
    }

    [TestMethod]
    [DataRow("DELETE FROM root_rescan_generations;")]
    [DataRow("UPDATE root_rescan_generations SET generation='00000000-0000-0000-0000-000000000000';")]
    [DataRow("UPDATE root_rescan_generations SET generation='invalid';")]
    [DataRow("UPDATE root_rescan_generations SET requested_utc='invalid';")]
    [DataRow("PRAGMA foreign_keys=OFF; DELETE FROM deferred_rescan_roots;")]
    public async Task CurrentSchemaRejectsMissingOrMalformedRootRequestsInsteadOfRepairingThem(string corruption)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.RequireRootRescanAsync(new(Guid.NewGuid()));
        await ExecuteSqlAsync(fixture.Paths, corruption);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(88L, await command.ExecuteScalarAsync());
    }

    [TestMethod]
    [DataRow("DELETE FROM full_rescan_request_snapshots;")]
    [DataRow("UPDATE full_rescan_request_snapshots SET generation='00000000-0000-0000-0000-000000000000';")]
    [DataRow("UPDATE full_rescan_request_snapshots SET generation='11111111-1111-1111-1111-111111111111';")]
    [DataRow("UPDATE full_rescan_root_requests SET generation='invalid';")]
    [DataRow("DELETE FROM full_rescan_root_requests;")]
    [DataRow("UPDATE full_rescan_root_requests SET root_id='11111111-1111-1111-1111-111111111111';")]
    [DataRow("UPDATE full_rescan_request_snapshots SET fingerprint=zeroblob(32);")]
    [DataRow("UPDATE full_rescan_root_requests SET requested_utc='2099-01-01T00:00:00.0000000+00:00';")]
    public async Task ProjectionRejectsMissingOrUnrelatedCapturedEvidence(string corruption)
    {
        using var fixture = new Fixture();
        Guid generation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.RequireRootRescanAsync(new(Guid.NewGuid()));
            generation = (await catalog.BeginFullRescanAsync()).Generation;
            await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Running);
            await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Acknowledging);
        }
        await ExecuteSqlAsync(fixture.Paths, corruption);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadCapturedRootRescanRequestsAsync(generation));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.CompleteFullRescanAsync(generation));
        Assert.IsNotNull(await reopened.ReadFullRescanAsync());
        Assert.HasCount(1, await reopened.ReadRootRescanRequestsAsync());
    }

    [TestMethod]
    public async Task EmptySnapshotIsExplicitAndCannotBeBorrowedByAnotherGeneration()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Guid generation = (await catalog.BeginFullRescanAsync()).Generation;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadCapturedRootRescanRequestsAsync(generation));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Acknowledging));
        await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Running);
        Assert.IsEmpty(await catalog.ReadCapturedRootRescanRequestsAsync(generation));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadCapturedRootRescanRequestsAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteFullRescanAsync(generation));
        await catalog.UpdateFullRescanAsync(generation, MirrorPulseFullRescanPhase.Acknowledging);
        await catalog.CompleteFullRescanAsync(generation);
        Assert.IsNull(await catalog.ReadFullRescanAsync());
    }

    private static SqliteConnection Connection(MirrorPulseStoragePaths paths) => new(new SqliteConnectionStringBuilder
    { DataSource = paths.ProductCatalogDatabasePath, Pooling = false, ForeignKeys = true }.ToString());

    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = Connection(paths);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths => new(Path.Combine(Directory, "sync"), Path.Combine(Directory, "data"));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
