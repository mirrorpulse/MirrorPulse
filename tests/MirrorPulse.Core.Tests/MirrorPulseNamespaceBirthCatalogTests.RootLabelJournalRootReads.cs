using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelPendingLookupWorkDoesNotGrowWithUnrelatedPendingRoots(bool existingRoot)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        RootId selected = existingRoot ? original.Witness.RootId : RootId.New();
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        await AddForeignRootLabelRowsAsync(connection, original.Observation.OperationId, 1, 128);
        var small = await MeasureRootLabelPendingReadAsync(catalog, connection, selected);
        await AddForeignRootLabelRowsAsync(connection, original.Observation.OperationId, 129, 4096);
        var large = await MeasureRootLabelPendingReadAsync(catalog, connection, selected);
        CollectionAssert.AreEqual(small.Operations.ToArray(), large.Operations.ToArray());
        Assert.HasCount(existingRoot ? 1 : 0, large.Operations);
        if (existingRoot) Assert.AreEqual(original, large.Operations[0]);
        Console.WriteLine($"RootLabelPointReadWork: existing={existingRoot}; foreignRootsSmall=128; foreignRootsLarge=4096; smallVmSteps={small.Steps}; largeVmSteps={large.Steps}; nativeExecution=False.");
        Assert.IsLessThanOrEqualTo(small.Steps + 64, large.Steps,
            "A selected-root lookup must not scan pending originals belonging to unrelated roots.");

        await using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN " + large.Sql;
        // Legacy SQLite trace expands parameters; supplying them also supports an unexpanded trace.
        plan.Parameters.AddWithValue("$root", selected.ToString());
        plan.Parameters.AddWithValue("$after", DBNull.Value);
        plan.Parameters.AddWithValue("$limit", 2);
        var details = new List<string>();
        await using (var reader = await plan.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Console.WriteLine("RootLabelPointReadPlan: " + string.Join("; ", details));
        Assert.IsTrue(details.Any(detail => detail.Contains("SEARCH root_label_journal_acknowledgements USING INDEX root_label_journal_pending_root (root_id=?)", StringComparison.Ordinal)));
        Assert.IsTrue(details.Any(detail => detail.Contains("SEARCH root_label_journal_acknowledgements USING INDEX root_label_journal_pending_payload_root (root_id=?)", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(detail => detail.Contains("SCAN root_label_journal_acknowledgements", StringComparison.Ordinal)));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RootLabelRecoveryCursorSeeksPastEarlierOwnersInsteadOfRescanningThem()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        Guid after = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await AddForeignRootLabelRowsAsync(connection, original.Observation.OperationId, 1, 128);
        var small = await MeasureRootLabelPendingReadAsync(catalog, connection, null, after);
        await AddForeignRootLabelRowsAsync(connection, original.Observation.OperationId, 129, 4096);
        var large = await MeasureRootLabelPendingReadAsync(catalog, connection, null, after);
        Assert.IsEmpty(small.Operations); Assert.IsEmpty(large.Operations);
        Console.WriteLine($"RootLabelCursorReadWork: foreignRootsSmall=128; foreignRootsLarge=4096; smallVmSteps={small.Steps}; largeVmSteps={large.Steps}; nativeExecution=False.");
        Assert.IsLessThanOrEqualTo(small.Steps + 64, large.Steps,
            "A canonical recovery cursor must seek past earlier originals rather than rescanning the pending prefix.");
        await using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN " + large.Sql;
        plan.Parameters.AddWithValue("$root", DBNull.Value);
        plan.Parameters.AddWithValue("$after", after.ToString("D"));
        plan.Parameters.AddWithValue("$limit", 1);
        var details = new List<string>();
        await using (var reader = await plan.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Console.WriteLine("RootLabelCursorReadPlan: " + string.Join("; ", details));
        foreach (string index in new[] { "root_label_journal_pending_status", "root_label_journal_pending_payload" })
            Assert.IsTrue(details.Any(detail =>
                detail.Contains($"SEARCH root_label_journal_acknowledgements USING INDEX {index} (operation_id>?)", StringComparison.Ordinal) ||
                detail.Contains($"SEARCH root_label_journal_acknowledgements USING COVERING INDEX {index} (operation_id>?)", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(detail => detail.Contains("SCAN root_label_journal_acknowledgements", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RootLabelPointReadDoesNotAdoptForeignDamageOrReplaceGlobalAdmissionValidation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await AddForeignRootLabelRowsAsync(connection, original.Observation.OperationId, 1, 1);
        }
        string before = await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths);
        Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingRootLabelJournalAcknowledgementAsync(
            new RootId(Guid.Parse("10000000-0000-0000-0000-000000000001"))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(before, await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths));
        Assert.IsNull((await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId))!.AcknowledgedAt);
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("status-completed")]
    [DataRow("status-pending")]
    public async Task RootLabelPointReadRetainsBothStatusAndPayloadCorruptionDetection(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        if (damage == "status-pending")
            await catalog.CompleteRootLabelJournalAcknowledgementAsync(original, original.StartedAt.AddSeconds(1));
        await ExecuteSqlAsync(fixture.Paths, "UPDATE root_label_journal_acknowledgements SET " + (damage switch
        {
            "fingerprint" => "fingerprint=zeroblob(32)",
            "status-completed" => "status=1",
            _ => "status=0",
        }) + ";");
        string before = await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingRootLabelJournalAcknowledgementsAsync());
        Assert.AreEqual(before, await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task RootLabelPointReadRejectsMultipleUnfinishedPayloadOwnersBeforeReturningOne()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, $"""
            INSERT INTO root_label_journal_acknowledgements
                (operation_id,root_id,member_id,label_operation_id,started_utc,status,payload,fingerprint)
            SELECT '{Guid.NewGuid():D}',root_id,member_id,label_operation_id,started_utc,1,payload,fingerprint
            FROM root_label_journal_acknowledgements WHERE operation_id='{original.Observation.OperationId:D}';
            """);
        string before = await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths);
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        StringAssert.Contains(failure.Message, "multiple unfinished original ACK owners");
        Assert.AreEqual(original, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        Assert.AreEqual(before, await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RootLabelDerivedRootIndexRebuildNeverChangesOriginalsAndRejectsSubstitution(bool substitute)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            original = await PrepareRootLabelJournalStartAsync(catalog);
        string before = await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX root_label_journal_pending_payload_root;" +
            (substitute ? "CREATE INDEX root_label_journal_pending_payload_root ON root_label_journal_acknowledgements(operation_id);" : ""));
        if (substitute)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        else
        {
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            Assert.AreEqual(original, await reopened.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        }
        Assert.AreEqual(before, await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(91L, await version.ExecuteScalarAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task<string> ReadRootLabelJournalOriginalBytesAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT operation_id||'|'||root_id||'|'||member_id||'|'||label_operation_id||'|'||started_utc||'|'||status||'|'||hex(payload)||'|'||hex(fingerprint)
            FROM root_label_journal_acknowledgements ORDER BY operation_id;
            """;
        var rows = new List<string>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return string.Join("\n", rows);
    }

    private static async Task AddForeignRootLabelRowsAsync(SqliteConnection connection, Guid original, int first, int last)
    {
        await using var command = connection.CreateCommand();
        // Deliberately inconsistent private rows must remain globally discoverable without
        // forcing another root's point lookup to read or reinterpret their payloads.
        command.CommandText = """
            WITH RECURSIVE ids(n) AS (SELECT $first UNION ALL SELECT n+1 FROM ids WHERE n<$last)
            INSERT INTO root_label_journal_acknowledgements
                (operation_id,root_id,member_id,label_operation_id,started_utc,status,payload,fingerprint)
            SELECT printf('00000000-0000-0000-0000-%012d',n),printf('10000000-0000-0000-0000-%012d',n),
                member_id,label_operation_id,started_utc,0,payload,fingerprint
            FROM ids CROSS JOIN root_label_journal_acknowledgements WHERE operation_id=$original;
            """;
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$last", last);
        command.Parameters.AddWithValue("$original", original.ToString("D"));
        Assert.AreEqual(last - first + 1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(IReadOnlyList<MirrorPulseRootLabelJournalAcknowledgement> Operations, int Steps, string Sql)>
        MeasureRootLabelPendingReadAsync(MirrorPulseProductCatalog catalog, SqliteConnection connection, RootId? root, Guid? after = null)
    {
        int steps = 0;
        var statements = new List<string>();
        SQLitePCL.delegate_progress progress = _ => { steps++; return 0; };
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.Contains("FROM root_label_journal_acknowledgements INDEXED BY", StringComparison.Ordinal)) statements.Add(sql);
        };
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 1, progress, null);
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        IReadOnlyList<MirrorPulseRootLabelJournalAcknowledgement> result;
        try
        {
            if (root is { } selected)
            {
                var pending = await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(selected);
                result = pending is null ? [] : [pending];
            }
            else result = await catalog.ReadPendingRootLabelJournalAcknowledgementsAsync(after, limit: 1);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 0, (SQLitePCL.delegate_progress)null!, null);
            GC.KeepAlive(trace); GC.KeepAlive(progress);
        }
        Assert.HasCount(1, statements, "Measure the actual production selected-root query.");
        Assert.IsGreaterThan(0, steps);
        return (result, steps, statements[0]);
    }
}
