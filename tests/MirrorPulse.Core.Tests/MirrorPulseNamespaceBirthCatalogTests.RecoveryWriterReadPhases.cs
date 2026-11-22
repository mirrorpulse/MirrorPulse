using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("NamespaceRecoveryWriterReadPhase")]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(true, 4)]
    [DataRow(false, 4)]
    public async Task RecoveryWritersRetainWarmHistoryAndReleaseReadFactsBeforeEveryMutation(bool enabled, int generations)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var action = setup.Action;
        var observations = new List<RecoveryWriterSql>();
        var history = new List<MirrorPulseNamespaceBornMemberRecoveryAction>();
        for (int generation = 1; generation <= generations; generation++)
        {
            observations.Add(await ObserveRecoveryWriterAsync(catalog, "prepare", enabled, generation,
                async () => Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
                    await catalog.PrepareNamespaceBornMemberRecoveryAsync(action)))));
            Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
                await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
            var protection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
            var completedAt = action.PreparedAt.AddSeconds(2);
            observations.Add(await ObserveRecoveryWriterAsync(catalog, "complete", enabled, generation,
                async () => Assert.AreEqual(action.ActionId,
                    (await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, completedAt)).ActionId)));
            observations.Add(await ObserveRecoveryWriterAsync(catalog, "completion-replay", enabled, generation,
                async () => Assert.AreEqual(completedAt,
                    (await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, completedAt)).CompletedAt)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
            history.Add(action);
            CollectionAssert.AreEqual(history.Select(SerializeBornOwnerTest).ToArray(),
                (await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(action.Child.Member.MemberId)).Select(SerializeBornOwnerTest).ToArray());
            Assert.AreEqual(action.ActionId, (await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId))!.ProtectionId);
            action = action with
            {
                ActionId = Guid.NewGuid(),
                PreviousActionId = action.ActionId,
                Child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(setup.Initializer, action.Child.Member.MemberId))[0],
                Parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(action.Parent.Context.Member.MemberId))!,
                PreparedAt = action.PreparedAt.AddSeconds(3)
            };
        }
        Assert.IsTrue(observations.All(row => row.ReadPhaseSelects > 0), "Each writer must validate fresh facts in a bounded read phase.");
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private sealed record RecoveryWriterSql(string Boundary, bool Enabled, int Generation, int Selects, int ReadPhaseSelects, int Writes);

    private static async Task<RecoveryWriterSql> ObserveRecoveryWriterAsync(MirrorPulseProductCatalog catalog, string boundary,
        bool enabled, int generation, Func<Task> action, Action? firstScopedRead = null)
    {
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var snapshots = (IEnumerable)typeof(MirrorPulseProductCatalog)
            .GetField("_namespaceRecoveryReadSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        int selects = 0, readPhaseSelects = 0, writes = 0, writesWithReadFacts = 0;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            bool active = HasActiveSnapshot();
            string statement = sql.TrimStart();
            if (statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selects++;
                if (active && ++readPhaseSelects == 1) firstScopedRead?.Invoke();
            }
            else if (statement.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                statement.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
                statement.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase))
            {
                writes++; if (active) writesWithReadFacts++;
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { await action(); }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        var result = new RecoveryWriterSql(boundary, enabled, generation, selects, readPhaseSelects, writes);
        Console.WriteLine("RecoveryWriterSql: " + JsonSerializer.Serialize(result));
        Assert.AreEqual(0, writesWithReadFacts, "No SQL mutation may run while successful current facts are cached.");
        Assert.IsFalse(HasActiveSnapshot(), "The writer must release current read facts before returning or throwing.");
        return result;

        bool HasActiveSnapshot() => snapshots.Cast<object>().Any(entry =>
            ((SqliteTransaction)entry.GetType().GetProperty("Key")!.GetValue(entry)!).Connection == connection);
    }
}
