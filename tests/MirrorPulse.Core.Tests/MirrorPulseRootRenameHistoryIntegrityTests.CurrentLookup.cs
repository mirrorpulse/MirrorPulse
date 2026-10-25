using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseRootRenameHistoryIntegrityTests
{
    [TestMethod]
    public async Task CurrentRootLookupUsesItsIndexedOwnerAndDoesNotReadUnrelatedCurrentRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        SqliteConnection connection = OriginalConnection(fixture.Catalog);
        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO managed_root_renames(root_id,payload) SELECT $root,payload FROM managed_root_renames WHERE root_id=$source;";
            insert.Parameters.AddWithValue("$root", RootId.New().ToString());
            insert.Parameters.AddWithValue("$source", fixture.Intent.RootId.ToString());
            Assert.AreEqual(1, await insert.ExecuteNonQueryAsync());
        }
        var statements = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { Assert.AreEqual(fixture.Intent, await fixture.Catalog.ReadManagedRootRenameAsync(fixture.Intent.RootId)); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        string[] reads = statements.Where(sql => sql.Contains(" FROM managed_root_renames ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, reads);
        await using (SqliteCommand plan = connection.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + reads[0];
            await using SqliteDataReader reader = await plan.ExecuteReaderAsync();
            var details = new List<string>();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            Assert.IsTrue(details.Any(detail => detail.StartsWith("SEARCH managed_root_renames ", StringComparison.Ordinal) &&
                detail.Contains("root_id=?", StringComparison.Ordinal)));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenamesAsync());
        Assert.AreEqual(fixture.Proof, (await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId))!.Proof);
        await fixture.ReopenAsync();
        Assert.AreEqual(fixture.Intent, await fixture.Catalog.ReadManagedRootRenameAsync(fixture.Intent.RootId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenamesAsync());
        Assert.AreEqual(fixture.Proof, (await fixture.Catalog.ReadManagedRootRenameHistoryAsync(fixture.Intent.OperationId))!.Proof);
    }

    [TestMethod]
    [DataRow("payload-root")]
    [DataRow("payload-phase")]
    public async Task CurrentRootLookupRejectsItsOwnDamagedOwnerOrPhaseWithoutChangingOriginals(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DamageAsync("managed_root_renames", damage);
        string current = await fixture.ReadRawAsync("managed_root_renames");
        string original = await fixture.ReadRawAsync("managed_root_rename_history");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameAsync(fixture.Intent.RootId));
        Assert.AreEqual(current, await fixture.ReadRawAsync("managed_root_renames"));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
        await fixture.ReopenAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameAsync(fixture.Intent.RootId));
        Assert.AreEqual(current, await fixture.ReadRawAsync("managed_root_renames"));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
    }

    [TestMethod]
    public async Task CurrentRootLookupRetainsTerminalResultsAndRejectsInvalidOrCancelledInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        var terminal = await fixture.Catalog.TransitionManagedRootRenameAsync(fixture.Intent.OperationId,
            MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        Assert.AreEqual(terminal, await fixture.Catalog.ReadManagedRootRenameAsync(terminal.RootId));
        Assert.IsNull(await fixture.Catalog.ReadManagedRootRenameAsync(RootId.New()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.ReadManagedRootRenameAsync(default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        string current = await fixture.ReadRawAsync("managed_root_renames");
        string original = await fixture.ReadRawAsync("managed_root_rename_history");
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.ReadManagedRootRenameAsync(terminal.RootId, canceled.Token));
        Assert.AreEqual(current, await fixture.ReadRawAsync("managed_root_renames"));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
    }
}
