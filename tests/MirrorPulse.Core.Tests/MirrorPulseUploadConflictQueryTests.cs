using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseUploadConflictQueryTests
{
    [TestMethod]
    public async Task PointLookupDoesNotDecodeAnotherInstancesBrokenConflict()
    {
        await using var fixture = await Fixture.OpenAsync();
        var expected = Conflict(InstanceId.New(), Guid.NewGuid(), Guid.NewGuid());
        await fixture.Catalog.SaveUploadConflictAsync(expected);
        await fixture.Catalog.SetUploadConflictStatusAsync(expected.ConflictId, MirrorPulseConflictStatus.Resolved);
        Guid broken = await fixture.AddBrokenConflictAsync();

        var actual = await fixture.Catalog.ReadUploadConflictAsync(expected.ConflictId);
        Assert.IsNotNull(actual); Assert.AreEqual(expected.InstanceId, actual.InstanceId);
        Assert.AreEqual(expected.ChangeId, actual.ChangeId); Assert.AreEqual(expected.RemoteRevision, actual.RemoteRevision);
        Assert.AreEqual(MirrorPulseConflictStatus.Resolved, actual.Status);
        Assert.IsNull(await fixture.Catalog.ReadUploadConflictAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<FormatException>(() => fixture.Catalog.ReadUploadConflictAsync(broken));
    }

    [TestMethod]
    public async Task EpochPagesRetainEveryStatusAndIsolateBothInstanceAndOriginalAcrossRestart()
    {
        await using var fixture = await Fixture.OpenAsync();
        var instance = InstanceId.New(); Guid original = Guid.NewGuid();
        var expected = new List<MirrorPulseConflictRecord>();
        for (int index = 0; index < 131; index++)
        {
            var conflict = Conflict(instance, original, Guid.NewGuid());
            expected.Add(conflict); await fixture.Catalog.SaveUploadConflictAsync(conflict);
            await fixture.Catalog.SetUploadConflictStatusAsync(conflict.ConflictId, (MirrorPulseConflictStatus)(index % 3));
        }
        await fixture.Catalog.SaveUploadConflictAsync(Conflict(InstanceId.New(), original, Guid.NewGuid()));
        await fixture.Catalog.SaveUploadConflictAsync(Conflict(instance, Guid.NewGuid(), Guid.NewGuid()));
        await fixture.AddBrokenConflictAsync(); await fixture.ReopenAsync();

        var received = new List<MirrorPulseConflictRecord>(); Guid? after = null;
        while (true)
        {
            var page = await fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, after, 64);
            Assert.IsLessThanOrEqualTo(64, page.Count);
            Assert.IsTrue(page.All(record => record.InstanceId == instance && record.ChangeId == original.ToString("D")));
            Assert.IsTrue(page.All(record => after is null || string.CompareOrdinal(record.ConflictId.ToString("D"), after.Value.ToString("D")) > 0));
            received.AddRange(page);
            if (page.Count < 64) break;
            after = page[^1].ConflictId;
        }
        CollectionAssert.AreEqual(expected.OrderBy(record => record.ConflictId.ToString("D"), StringComparer.Ordinal).Select(record => record.ConflictId).ToArray(),
            received.Select(record => record.ConflictId).ToArray());
        Assert.HasCount(3, received.Select(record => record.Status).Distinct());
        Assert.IsEmpty(await fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, received[^1].ConflictId));
        Assert.IsEmpty(await fixture.Catalog.ReadUploadConflictEpochsAsync(InstanceId.New(), original));
    }

    [TestMethod]
    public async Task EpochCursorSurvivesStatusChangesWithoutOffsetGaps()
    {
        await using var fixture = await Fixture.OpenAsync();
        var instance = InstanceId.New(); Guid original = Guid.NewGuid();
        Guid[] ids = [Guid.Parse("10000000-0000-4000-8000-000000000001"), Guid.Parse("20000000-0000-4000-8000-000000000002"),
            Guid.Parse("30000000-0000-4000-8000-000000000003")];
        foreach (Guid id in ids) await fixture.Catalog.SaveUploadConflictAsync(Conflict(instance, original, id));
        var first = await fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, limit: 1);
        await fixture.Catalog.SetUploadConflictStatusAsync(ids[0], MirrorPulseConflictStatus.Resolved);
        await fixture.Catalog.SetUploadConflictStatusAsync(ids[1], MirrorPulseConflictStatus.Dismissed);
        var next = await fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, first.Single().ConflictId, 2);
        CollectionAssert.AreEqual(ids[1..], next.Select(record => record.ConflictId).ToArray());
        Assert.AreEqual(MirrorPulseConflictStatus.Dismissed, next[0].Status);
        Assert.AreEqual(MirrorPulseConflictStatus.Pending, next[1].Status);
    }

    [TestMethod]
    public async Task Schema57UpgradePreservesConflictsAndCreatesASeekableEpochIndex()
    {
        await using var fixture = await Fixture.OpenAsync();
        var expected = Conflict(InstanceId.New(), Guid.NewGuid(), Guid.NewGuid());
        await fixture.Catalog.SaveUploadConflictAsync(expected); await fixture.Catalog.DisposeAsync();
        await using (var connection = await fixture.ConnectAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP INDEX ix_upload_conflicts_operation_epoch; PRAGMA user_version=57;";
            await downgrade.ExecuteNonQueryAsync();
        }
        await fixture.ReopenAsync();
        Assert.AreEqual(expected, (await fixture.Catalog.ReadUploadConflictEpochsAsync(expected.InstanceId, Guid.Parse(expected.ChangeId))).Single());
        await using var inspect = await fixture.ConnectAsync();
        await using var version = inspect.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(61L, await version.ExecuteScalarAsync());
        await using var plan = inspect.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN SELECT conflict_id FROM upload_conflicts
            WHERE change_id=$operation AND instance_id=$instance AND conflict_id>$after
            ORDER BY conflict_id COLLATE BINARY LIMIT 64;
            """;
        plan.Parameters.AddWithValue("$operation", expected.ChangeId); plan.Parameters.AddWithValue("$instance", expected.InstanceId.ToString());
        plan.Parameters.AddWithValue("$after", string.Empty);
        await using var reader = await plan.ExecuteReaderAsync(); var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.IsTrue(details.Any(detail => detail.Contains("SEARCH upload_conflicts USING COVERING INDEX ix_upload_conflicts_operation_epoch", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(257)]
    [DataRow(int.MaxValue)]
    public async Task EpochQueryRejectsUnboundedPageRequests(int limit)
    {
        await using var fixture = await Fixture.OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Catalog.ReadUploadConflictEpochsAsync(InstanceId.New(), Guid.NewGuid(), limit: limit));
    }

    [TestMethod]
    public async Task EpochQueryRejectsEmptyOperationAndCursorAndObservesCancellation()
    {
        await using var fixture = await Fixture.OpenAsync(); var instance = InstanceId.New(); Guid original = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadUploadConflictEpochsAsync(instance, Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadUploadConflictAsync(Guid.Empty));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.ReadUploadConflictEpochsAsync(instance, original, cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.ReadUploadConflictAsync(Guid.NewGuid(), cancelled.Token));
    }

    private static MirrorPulseConflictRecord Conflict(InstanceId instance, Guid original, Guid id) => new(id, instance,
        original.ToString("D"), "Docs/content.bin", MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged,
        "base", "changed", DateTimeOffset.UnixEpoch);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture(); fixture.Paths = new(Path.Combine(fixture._root, "sync"), Path.Combine(fixture._root, "data"));
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); return fixture;
        }
        public async Task ReopenAsync()
        {
            await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
        }
        public async Task<SqliteConnection> ConnectAsync()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); return connection;
        }
        public async Task<Guid> AddBrokenConflictAsync()
        {
            Guid id = Guid.NewGuid(); await using var connection = await ConnectAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO upload_conflicts(conflict_id,instance_id,change_id,relative_path,reason,version_comparison,detected_utc,status)
                VALUES($id,'invalid-instance','unrelated','Other/file',0,0,'not-a-date',0);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D")); await insert.ExecuteNonQueryAsync(); return id;
        }
        public async ValueTask DisposeAsync()
        {
            if (Catalog is not null) await Catalog.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
