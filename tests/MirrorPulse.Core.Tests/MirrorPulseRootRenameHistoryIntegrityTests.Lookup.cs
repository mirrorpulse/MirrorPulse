using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseRootRenameHistoryIntegrityTests
{
    [TestMethod]
    public async Task HistoricalOperationUsesOneIndexedReadAndRetainsAllRequestedHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Catalog.TransitionManagedRootRenameAsync(fixture.Intent.OperationId,
            MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        Guid later = Guid.Empty;
        for (int index = 1; index <= 33; index++)
        {
            var next = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Intent.RootId,
                "Later " + index.ToString(CultureInfo.InvariantCulture));
            await fixture.Catalog.SaveManagedRootRenameProofAsync(next.OperationId, fixture.Proof);
            await fixture.Catalog.TransitionManagedRootRenameAsync(next.OperationId,
                MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
            later = next.OperationId;
        }
        var all = await fixture.Catalog.ReadManagedRootRenameHistoryAsync();
        Assert.HasCount(34, all);
        MirrorPulseRootRenameHistory expected = all.Single(item => item.Intent.OperationId == fixture.Intent.OperationId);
        SqliteConnection connection = OriginalConnection(fixture.Catalog);
        string original = await ReadOriginalRowsAsync(connection);
        var statements = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { Assert.AreEqual(expected, await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId)); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        string[] reads = statements.Where(sql => sql.Contains(" FROM managed_root_rename_history ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, reads);
        await using (SqliteCommand plan = connection.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + reads[0];
            await using SqliteDataReader reader = await plan.ExecuteReaderAsync();
            var details = new List<string>();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            Assert.IsTrue(details.Any(detail => detail.StartsWith("SEARCH managed_root_rename_history ", StringComparison.Ordinal) &&
                detail.Contains("operation_id=?", StringComparison.Ordinal)), "The actual retained operation must use its indexed key.");
        }
        Assert.AreEqual(original, await ReadOriginalRowsAsync(connection));
        await using (SqliteCommand damage = connection.CreateCommand())
        {
            damage.CommandText = "UPDATE managed_root_rename_history SET proof='null' WHERE operation_id=$operation;";
            damage.Parameters.AddWithValue("$operation", later.ToString("D"));
            Assert.AreEqual(1, await damage.ExecuteNonQueryAsync());
        }
        string damaged = await ReadOriginalRowsAsync(connection);
        Assert.AreEqual(expected, await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        Assert.AreEqual(damaged, await ReadOriginalRowsAsync(connection));
        await fixture.ReopenAsync();
        Assert.AreEqual(expected, await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        Assert.AreEqual(damaged, await ReadOriginalRowsAsync(OriginalConnection(fixture.Catalog)));
    }

    [TestMethod]
    [DataRow("owner-root")]
    [DataRow("payload-operation")]
    [DataRow("proof-null-json")]
    [DataRow("proof-evidence")]
    public async Task SelectedDamagedOperationStillRejectsWithoutAdoptingAnotherHistory(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual(fixture.Proof, (await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId))!.Proof);
        await fixture.DamageAsync("managed_root_rename_history", damage);
        string original = await fixture.ReadRawAsync("managed_root_rename_history");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
        await fixture.ReopenAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
    }

    [TestMethod]
    public async Task MissingCancelledAndInvalidLookupInputsDoNotChangeOriginals()
    {
        await using var fixture = await Fixture.CreateAsync();
        string original = await fixture.ReadRawAsync("managed_root_rename_history");
        Assert.IsNull(await fixture.Catalog.ReadManagedRootRenameHistoryAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync(Guid.Empty));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId, canceled.Token));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
    }

    private static SqliteConnection OriginalConnection(MirrorPulseProductCatalog catalog) =>
        (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;

    private static async Task<string> ReadOriginalRowsAsync(SqliteConnection connection)
    {
        await using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT json_group_array(json_array(operation_id,root_id,payload,proof,observation)) FROM
            (SELECT operation_id,root_id,payload,proof,observation FROM managed_root_rename_history ORDER BY rowid);
            """;
        return (string)(await query.ExecuteScalarAsync())!;
    }
}
