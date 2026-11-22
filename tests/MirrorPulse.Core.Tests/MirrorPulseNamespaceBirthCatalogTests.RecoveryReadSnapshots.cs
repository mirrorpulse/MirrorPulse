using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("NamespaceRecoveryReadSnapshot")]
    public async Task BornRecoveryReadsRetainTheExactActionAndPerformNoCatalogWrites()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(setup.Action);
        foreach (string boundary in new[] { "recovery-lineage", "protection-snapshot", "execution-admission" })
        {
            await ObserveRecoveryReadSqlAsync(catalog, boundary, async () =>
            {
                if (boundary == "recovery-lineage")
                    Assert.AreEqual(SerializeBornOwnerTest(setup.Action.Child), SerializeBornOwnerTest(
                        (await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, setup.Action.Child.Member.MemberId))[0]));
                else if (boundary == "protection-snapshot")
                    Assert.AreEqual(SerializeBornOwnerTest(setup.Action.Parent), SerializeBornOwnerTest(
                        await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Action.Parent.Context.Member.MemberId)));
                else
                    Assert.AreEqual(SerializeBornOwnerTest(setup.Action), SerializeBornOwnerTest(
                        await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(setup.Action.ActionId)));
            });
        }
        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(setup.Action.ActionId));
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)).Completion);
        Assert.AreEqual(setup.Tree.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Tree.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceRecoveryReadSnapshot")]
    [DataRow("member")]
    [DataRow("initializer")]
    [DataRow("reservation")]
    [DataRow("child-name")]
    [DataRow("parent-name")]
    [DataRow("action")]
    public async Task APreviousReadCannotHideDamagedFactsInTheNextAdmission(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        for (int attempt = 0; attempt < 2; attempt++)
            Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
                await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "member" => $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{action.Child.Member.MemberId:D}';",
            "initializer" => $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Initializer:D}';",
            "reservation" => $"DELETE FROM namespace_initialization_roots WHERE operation_id='{setup.Initializer:D}';",
            "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Child.Member.MemberId:D}';",
            "parent-name" => $"DELETE FROM namespace_member_names WHERE member_id='{action.Parent.Context.Member.MemberId:D}';",
            "action" => $"UPDATE namespace_born_member_recovery_actions SET fingerprint=zeroblob(32) WHERE action_id='{action.ActionId:D}';",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$id", action.ActionId.ToString("D"));
            command.CommandText = "SELECT COUNT(*) FROM namespace_born_member_recovery_completions WHERE action_id=$id;";
            Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM namespace_born_member_recovery_pending WHERE action_id=$id;";
            Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceRecoveryReadSnapshot")]
    public async Task ACompletedActionCannotReuseItsEarlierExecutionAdmission()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
            await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var completed = await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId,
            LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1)), true, action.PreparedAt.AddSeconds(2));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        Assert.AreEqual(completed, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(setup.Owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Tree.Move.OperationId));
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)).Completion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task ObserveRecoveryReadSqlAsync(MirrorPulseProductCatalog catalog, string boundary, Func<Task> read)
    {
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        long changes = TotalChanges();
        var statements = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (context, sql) =>
        {
            string normalized = Regex.Replace(sql, "'[^']*'", "?").Trim();
            statements[normalized] = statements.GetValueOrDefault(normalized) + 1;
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        var stopwatch = Stopwatch.StartNew();
        try { await read(); }
        finally
        {
            stopwatch.Stop();
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        Assert.AreEqual(changes, TotalChanges(), "Reading recovery inputs must not modify catalog state.");
        Console.WriteLine("NamespaceRecoveryReadSql: " + JsonSerializer.Serialize(new
        {
            boundary,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            selectStatements = statements.Where(pair => pair.Key.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value),
            mostRepeated = statements.OrderByDescending(pair => pair.Value).Take(8).ToArray()
        }));

        long TotalChanges()
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT total_changes();";
            return (long)command.ExecuteScalar()!;
        }
    }
}
