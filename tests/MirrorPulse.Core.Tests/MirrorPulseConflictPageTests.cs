using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseConflictPageTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(7)]
    public async Task BothPendingFamiliesKeepExactUtcTicksAndDeterministicTiesAcrossEveryPage(int size)
    {
        await using var fixture = await Fixture.OpenAsync();
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(9_999_999);
        var records = new List<MirrorPulseConflictRecord>();
        for (int index = 0; index < 12; index++)
        {
            var detected = instant.AddTicks(index / 4).ToOffset(TimeSpan.FromHours(index % 2 == 0 ? 8 : -7));
            var record = Record(index + 1, detected, index % 3 == 0); records.Add(record); await fixture.SaveAsync(record);
        }
        var resolved = Record(100, instant.AddDays(1), remote: false); await fixture.SaveAsync(resolved);
        await fixture.Catalog.SetUploadConflictStatusAsync(resolved.ConflictId, MirrorPulseConflictStatus.Resolved);
        var expected = records.OrderByDescending(record => record.DetectedAt).ThenBy(record => record.Source == MirrorPulseConflictSource.CfSharpRemote ? 0 : 1)
            .ThenBy(record => record.ConflictId.ToString("D"), StringComparer.Ordinal).ToArray();
        var actual = new List<MirrorPulseConflictRecord>(); string? cursor = null; var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await fixture.Catalog.ReadPendingConflictPageAsync(size, cursor); Assert.IsLessThanOrEqualTo(size, page.Items.Count);
            actual.AddRange(page.Items); cursor = page.NextCursor;
            if (cursor is not null) Assert.IsTrue(cursors.Add(cursor), "Every returned cursor must advance.");
        } while (cursor is not null);
        CollectionAssert.AreEqual(expected.Select(Key).ToArray(), actual.Select(Key).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task ResolvingAnEarlierPageAndInsertingANewerConflictCannotShiftTheRemainingOriginals()
    {
        await using var fixture = await Fixture.OpenAsync(); var start = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(1, 8).Select(index => Record(index, start.AddTicks(index), remote: false)).ToArray();
        foreach (var record in records) await fixture.SaveAsync(record);
        var first = await fixture.Catalog.ReadPendingConflictPageAsync(3);
        foreach (var record in first.Items) await fixture.Catalog.SetUploadConflictStatusAsync(record.ConflictId, MirrorPulseConflictStatus.Resolved);
        var newer = Record(100, start.AddDays(1), remote: true); await fixture.SaveAsync(newer);
        var remainder = new List<MirrorPulseConflictRecord>(); string? cursor = first.NextCursor;
        do { var page = await fixture.Catalog.ReadPendingConflictPageAsync(3, cursor); remainder.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
        CollectionAssert.AreEqual(records.Reverse().Skip(3).Select(Key).ToArray(), remainder.Select(Key).ToArray());
        Assert.AreEqual(newer.ConflictId, (await fixture.Catalog.ReadPendingConflictPageAsync(1)).Items[0].ConflictId);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public async Task InvalidPageSizesAreRejectedBeforeReadingTheCatalog(int size)
    {
        await using var fixture = await Fixture.OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Catalog.ReadPendingConflictPageAsync(size));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("100")]
    [DataRow("2:1:0:00000001-0000-0000-0000-000000000001")]
    [DataRow("1:01:0:00000001-0000-0000-0000-000000000001")]
    [DataRow("1:3155378976000000000:0:00000001-0000-0000-0000-000000000001")]
    [DataRow("1:1:2:00000001-0000-0000-0000-000000000001")]
    [DataRow("1:1:0:00000000-0000-0000-0000-000000000000")]
    public async Task InvalidOrUnsupportedCursorCannotSilentlyRestartTheFirstPage(string cursor)
    {
        await using var fixture = await Fixture.OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadPendingConflictPageAsync(cursor: cursor));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidPersistedTimeCannotDisappearBehindThePagingIndex(bool remote)
    {
        await using var fixture = await Fixture.OpenAsync(); await fixture.SaveAsync(Record(1, DateTimeOffset.UtcNow, remote));
        var connection = Connection(fixture.Catalog); await using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE " + (remote ? "remote_conflict_projections" : "upload_conflicts") + " SET detected_utc='invalid';";
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadPendingConflictPageAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualContinuedPageQueriesUseRangeIndexesWithoutAFullSort(bool crossesTimestamp)
    {
        await using var fixture = await Fixture.OpenAsync(); var now = DateTimeOffset.UtcNow;
        for (int index = 1; index <= (crossesTimestamp ? 4 : 12); index++)
            await fixture.SaveAsync(Record(index, crossesTimestamp && index == 4 ? now.AddTicks(-1) : now, crossesTimestamp || index % 2 == 0));
        var first = await fixture.Catalog.ReadPendingConflictPageAsync(2); Assert.IsNotNull(first.NextCursor);
        var connection = Connection(fixture.Catalog); var statements = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) => { if (sql.Contains("ORDER BY", StringComparison.Ordinal) && sql.Contains("LIMIT ", StringComparison.Ordinal)) statements.Add(sql); };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { Assert.HasCount(2, (await fixture.Catalog.ReadPendingConflictPageAsync(2, first.NextCursor)).Items); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Assert.HasCount(crossesTimestamp ? 3 : 2, statements, "At most two range seeks share one family row budget; the other family requires one.");
        var parts = first.NextCursor.Split(':');
        foreach (string statement in statements)
        {
            await using var explain = connection.CreateCommand(); explain.CommandText = "EXPLAIN QUERY PLAN " + statement;
            explain.Parameters.AddWithValue("$order", -long.Parse(parts[1], CultureInfo.InvariantCulture));
            explain.Parameters.AddWithValue("$after", parts[3]); explain.Parameters.AddWithValue("$limit", 3);
            await using var rows = await explain.ExecuteReaderAsync(); var details = new List<string>();
            while (await rows.ReadAsync()) details.Add(rows.GetString(3));
            string plan = string.Join("|", details); StringAssert.Contains(plan, "SEARCH");
            StringAssert.Contains(plan, statement.Contains("FROM upload_conflicts", StringComparison.Ordinal) ? "ix_upload_conflicts_page" : "ix_remote_conflicts_page");
            Console.WriteLine($"ConflictPagePlan: crossesTimestamp={crossesTimestamp}; plan={plan}");
            Assert.IsFalse(plan.Contains("TEMP B-TREE", StringComparison.Ordinal), plan);
        }
    }

    private static string Key(MirrorPulseConflictRecord value) =>
        $"{value.ConflictId:D}|{value.Source}|{value.DetectedAt:O}|{value.RelativePath}|{value.LocalRevision}|{value.RemoteRevision}|{value.Status}";

    private static MirrorPulseConflictRecord Record(int index, DateTimeOffset detected, bool remote) => new(
        Guid.Parse(index.ToString("x8", CultureInfo.InvariantCulture) + "-0000-0000-0000-000000000001"), InstanceId.New(), "change-" + index,
        "Docs/synthetic-" + index + ".txt", MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, "local", "remote", detected,
        source: remote ? MirrorPulseConflictSource.CfSharpRemote : MirrorPulseConflictSource.Upload);

    private static SqliteConnection Connection(MirrorPulseProductCatalog catalog) => (SqliteConnection)typeof(MirrorPulseProductCatalog)
        .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;

    private sealed class Fixture(string root, MirrorPulseStoragePaths paths, MirrorPulseProductCatalog catalog) : IAsyncDisposable
    {
        internal MirrorPulseStoragePaths Paths { get; } = paths;
        internal MirrorPulseProductCatalog Catalog { get; } = catalog;
        internal static async Task<Fixture> OpenAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-page-" + Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            return new(root, paths, await MirrorPulseProductCatalog.OpenAsync(paths));
        }
        internal Task SaveAsync(MirrorPulseConflictRecord record) => record.Source == MirrorPulseConflictSource.Upload
            ? Catalog.SaveUploadConflictAsync(record) : Catalog.SaveRemoteConflictProjectionAsync(record);
        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync();
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "MirrorPulse-conflict-page-";
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned fixture escaped its temporary parent.");
            Directory.Delete(root, recursive: true);
        }
    }
}
