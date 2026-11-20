using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task LocatedRecoveryHistoryReportsItsActualTransactionGraphAmplification()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLocatedRecoveryHistoryAsync(catalog);
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var statements = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements[sql] = statements.GetValueOrDefault(sql) + 1;
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        var timer = Stopwatch.StartNew();
        try
        {
            var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.MemberId);
            Assert.HasCount(5, history); Assert.AreEqual(setup.LastActionId, history[^1].ActionId);
        }
        finally { timer.Stop(); SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Console.WriteLine($"LocatedRecoveryGraphProfile: completedActions=5; statements={statements.Values.Sum()}; distinct={statements.Count}; elapsedMs={timer.ElapsedMilliseconds}; nativeExecuted=False.");
        foreach (string table in new[] { "namespace_birth_protections", "namespace_birth_observations", "namespace_birth_intents", "namespace_members",
            "namespace_member_positions", "namespace_member_moves", "namespace_initialization_intents", "namespace_born_member_recovery_actions",
            "namespace_born_member_recovery_completions", "namespace_permission_changes" })
            Console.WriteLine($"LocatedRecoveryGraphTableReads: table={table}; statements={statements.Where(entry => entry.Key.Contains("FROM " + table, StringComparison.Ordinal)).Sum(entry => entry.Value)}.");
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        int positionReads = statements.Where(entry => entry.Key.Contains("FROM namespace_member_positions", StringComparison.Ordinal)).Sum(entry => entry.Value);
        Assert.IsLessThanOrEqualTo(30, positionReads,
            "Five completed recovery epochs must not repeatedly traverse their already verified historical placement graphs within one transaction.");
    }

    [TestMethod]
    [DataRow("protection")]
    [DataRow("member")]
    [DataRow("position")]
    [DataRow("completion")]
    public async Task LocatedGraphReuseCannotHideCorruptionFromTheNextTransaction(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLocatedRecoveryHistoryAsync(catalog);
        Assert.HasCount(5, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.MemberId));
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "protection" => "UPDATE namespace_birth_protections SET fingerprint=zeroblob(32);",
            "member" => "UPDATE namespace_members SET fingerprint=zeroblob(32);",
            "position" => "UPDATE namespace_member_positions SET fingerprint=zeroblob(32);",
            _ => "UPDATE namespace_born_member_recovery_completions SET fingerprint=zeroblob(32);"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.MemberId));
    }

    private static async Task<(Guid MemberId, Guid LastActionId)> PrepareLocatedRecoveryHistoryAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareMovableMemberAsync(catalog); await CompleteMoveChainStepAsync(catalog, setup.Move);
        await RotateContextOriginalParentsAsync(catalog, setup, setup.Move.PreparedAt.AddSeconds(5));
        Guid? initialization = null, previous = null;
        DateTimeOffset prepared = setup.Move.PreparedAt.AddSeconds(10);
        for (int index = 0; index < 5; index++)
        {
            var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, prepared, initialization, previous);
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            var parent = action.Parent.Context;
            var protection = action.Child.BirthProtection! with
            {
                Version = 3,
                ProtectionId = action.ActionId,
                PreviousProtectionId = action.Child.BirthProtection!.ProtectionId,
                RoleSid = parent.Protection.RoleSid,
                Verification = action.Child.BirthProtection.Verification with { Dacl = action.TargetDacl, ObservedAt = prepared.AddSeconds(1) },
                Placement = new(action.Child.Member.MemberId, action.Child.Location.CurrentPositionOperationId,
                    parent.Member.MemberId, parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId, action.Child.Location.RelativePath)
            }; // Synthetic post-scope facts exercise catalog validation, not native acceptance.
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, protection, true, prepared.AddSeconds(2));
            initialization = action.InitializationOperationId; previous = action.ActionId; prepared = prepared.AddSeconds(3);
        }
        return (setup.Member.MemberId, previous!.Value);
    }
}
