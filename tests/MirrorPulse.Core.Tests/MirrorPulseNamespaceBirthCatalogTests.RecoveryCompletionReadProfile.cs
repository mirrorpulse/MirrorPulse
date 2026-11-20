using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("BornRecoveryCompletionProfile")]
    public async Task LocatedBornRecoveryCompletionReadsImmutablePositionsOnceAndKeepsCurrentHeadsLive()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLocatedRecoveryHistoryAsync(catalog);
        var previous = (await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.MemberId))[^1];
        var member = (await catalog.ReadNamespaceMemberAsync(setup.MemberId))!;
        var action = await RegisteredRecoveryActionAsync(catalog, member, previous.PreparedAt.AddSeconds(3),
            previous.InitializationOperationId, previous.ActionId);
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var parent = action.Parent.Context;
        var protection = action.Child.BirthProtection! with
        {
            Version = 3,
            ProtectionId = action.ActionId,
            PreviousProtectionId = action.Child.BirthProtection!.ProtectionId,
            RoleSid = parent.Protection.RoleSid,
            Verification = action.Child.BirthProtection.Verification with
            { Dacl = action.TargetDacl, ObservedAt = action.PreparedAt.AddSeconds(1) },
            Placement = new(action.Child.Member.MemberId, action.Child.Location.CurrentPositionOperationId,
                parent.Member.MemberId, parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId,
                action.Child.Location.RelativePath)
        }; // Synthetic scope facts exercise the real catalog commit, not native recovery.
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var statements = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements[sql] = statements.GetValueOrDefault(sql) + 1;
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        var timer = Stopwatch.StartNew();
        MirrorPulseNamespaceBornMemberRecoveryCompletion completion;
        try
        {
            completion = await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true,
                action.PreparedAt.AddSeconds(2));
        }
        finally
        {
            timer.Stop();
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        int positionReads = statements.Where(entry => entry.Key.Contains("FROM namespace_member_positions", StringComparison.Ordinal))
            .Sum(entry => entry.Value);
        int historicalRowReads = statements.Where(entry => entry.Key.Contains(
            "SELECT member_id,operation_id,previous_operation_id,payload,fingerprint FROM namespace_member_positions WHERE member_id=", StringComparison.Ordinal))
            .Sum(entry => entry.Value);
        int currentHeadReads = statements.Where(entry => entry.Key.Contains(
            "SELECT p.operation_id FROM namespace_member_positions p WHERE p.member_id=", StringComparison.Ordinal))
            .Sum(entry => entry.Value);
        int currentCountReads = statements.Where(entry => entry.Key.Contains(
            "SELECT COUNT(*) FROM namespace_member_positions WHERE member_id=", StringComparison.Ordinal))
            .Sum(entry => entry.Value);
        Console.WriteLine($"LocatedRecoveryCommitProfile: completedPredecessors=5; statements={statements.Values.Sum()}; distinct={statements.Count}; positionReads={positionReads}; historicalRowReads={historicalRowReads}; currentHeadReads={currentHeadReads}; currentCountReads={currentCountReads}; elapsedMs={timer.ElapsedMilliseconds}; nativeExecuted=False.");
        foreach (string table in new[] { "namespace_birth_protections", "namespace_members", "namespace_member_positions",
            "namespace_member_moves", "namespace_born_member_recovery_actions", "namespace_born_member_recovery_completions" })
            Console.WriteLine($"LocatedRecoveryCommitTableReads: table={table}; statements={statements.Where(entry => entry.Key.Contains("FROM " + table, StringComparison.Ordinal)).Sum(entry => entry.Value)}.");
        Assert.AreEqual(1, historicalRowReads,
            "The same immutable historical position must be read once in the sixth epoch's write transaction.");
        Assert.IsGreaterThan(0, currentHeadReads, "Current heads remain a separate live selection.");
        Assert.IsGreaterThan(0, currentCountReads, "Current history counts remain separately checked.");
        Assert.AreEqual(completion, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(protection, await catalog.ReadLatestNamespaceBirthProtectionAsync(member.OriginId));
        Assert.AreEqual(action.ActionId, (await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(member.MemberId))[^1].ActionId);
        CollectionAssert.AreEqual(new[] { member.RootId!.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
