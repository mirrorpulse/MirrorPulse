using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("intent-index")]
    [DataRow("observation-index")]
    [DataRow("plan")]
    [DataRow("start")]
    public async Task ReusingBirthRowsCannotHideChangedCanonicalIndexesOrTheirOriginalReferencesInTheNextTransaction(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        _ = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId);
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "intent-index" => "UPDATE namespace_birth_intents SET relative_path_key='DOCS/SUBSTITUTED';",
            "observation-index" => "UPDATE namespace_birth_observations SET item_id='00000000-0000-0000-0000-000000000001';",
            "plan" => "UPDATE namespace_birth_plans SET fingerprint=zeroblob(32);",
            _ => "UPDATE namespace_birth_starts SET fingerprint=zeroblob(32);"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task RegisteredRecoveryHistoryReportsItsActualTransactionReadAmplification()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5));
        for (int index = 0; index < 5; index++)
        {
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            if (index != 4) action = action with { ActionId = Guid.NewGuid(), PreviousActionId = action.ActionId, PreparedAt = action.PreparedAt.AddSeconds(1) };
        }
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var statements = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements[sql] = statements.GetValueOrDefault(sql) + 1;
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try
        {
            Assert.HasCount(5, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.Member.MemberId));
        }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Console.WriteLine($"RegisteredRecoveryReadProfile: history=5; statements={statements.Values.Sum()}; distinct={statements.Count}; nativeExecuted=False.");
        foreach (string table in new[] { "namespace_birth_protections", "namespace_birth_observations", "namespace_birth_intents", "namespace_members",
            "namespace_born_member_recovery_actions", "namespace_born_member_recovery_completions", "namespace_permission_changes" })
            Console.WriteLine($"RegisteredRecoveryTableReads: table={table}; statements={statements.Where(entry => entry.Key.Contains("FROM " + table, StringComparison.Ordinal)).Sum(entry => entry.Value)}.");
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.AreEqual(1, statements.Where(entry => entry.Key.Contains("FROM namespace_birth_observations WHERE operation_id=", StringComparison.Ordinal)).Sum(entry => entry.Value),
            "One transaction must read each immutable first observation only once while preserving reference checks.");
        Assert.AreEqual(1, statements.Where(entry => entry.Key.Contains("FROM namespace_birth_intents WHERE operation_id=", StringComparison.Ordinal)).Sum(entry => entry.Value),
            "One transaction must read each immutable birth admission only once while preserving its index checks.");
        int historicalPermissionReads = statements.Where(entry => entry.Key.Contains("FROM namespace_permission_changes", StringComparison.Ordinal) &&
            entry.Key.Contains("WHERE c.operation_id=", StringComparison.Ordinal)).Sum(entry => entry.Value);
        Console.WriteLine($"VerifiedPermissionReadBudget: historicalOperationReads={historicalPermissionReads}; latestHeadReads=" +
            $"{statements.Where(entry => entry.Key.Contains("FROM namespace_permission_changes", StringComparison.Ordinal) && entry.Key.Contains("ORDER BY c.rowid DESC", StringComparison.Ordinal)).Sum(entry => entry.Value)}.");
        // This member retains two captured originals: its direct parent and the anchor.
        Assert.AreEqual(2, historicalPermissionReads, "Each verified historical permission row should be read once; current-head queries must still run.");
    }
}
