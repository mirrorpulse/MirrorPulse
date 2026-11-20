using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseRemoteDeleteResolutionStatus.Applied)]
    [DataRow(MirrorPulseRemoteDeleteResolutionStatus.AlreadyApplied)]
    [DataRow(MirrorPulseRemoteDeleteResolutionStatus.Reconciled)]
    public async Task ACompletedDeletePublishesItsLostCommandReceiptWithoutAnotherSdkOrNativeInspection(MirrorPulseRemoteDeleteResolutionStatus disposition)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan) with { Status = disposition });
            _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
            _ = await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
            Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var center = new MirrorPulse.Core.Conflicts.MirrorPulseConflictCenter(); center.Upsert(plan.Decision.Conflict);
        int calls = 0;
        var actions = new MirrorPulse.CloudFiles.CfSharp.MirrorPulseRemoteConflictActions((_, _, _) =>
        { calls++; return ValueTask.FromException<CfSharp.CloudRemoteApplyEntryStatus>(new InvalidOperationException("The completed deletion has no SDK conflict to invoke again.")); },
            new(fixture.Paths), reopened, center);
        var result = await actions.ApplyAsync(plan.Decision.CommandId, plan.Decision.Conflict, plan.Decision.Action);
        Assert.IsTrue(result.Resolved); Assert.AreEqual(0, calls);
        Assert.AreEqual(disposition == MirrorPulseRemoteDeleteResolutionStatus.Applied ? CfSharp.CloudRemoteApplyEntryStatus.Applied :
            CfSharp.CloudRemoteApplyEntryStatus.AlreadyApplied, result.CfSharpStatus);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
        Assert.IsNotNull(await reopened.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId)); Assert.HasCount(0, center.Query());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task DeleteRecoveryDiscoveryRetainsItsOriginalCommandUntilItsSeparateReceiptCommits()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var root = plan.Entries[0].Original.Context.Member.RootId!.Value;
        Assert.AreEqual(plan.Decision, (await reopened.ReadRemoteDeleteResolutionForConflictAsync(plan.Decision.Conflict.ConflictId))!.Plan.Decision);
        Assert.AreEqual(plan.Decision, (await reopened.ReadPendingRemoteDeleteResolutionsAsync(root, limit: 1)).Single().Plan.Decision);
        Assert.HasCount(0, await reopened.ReadPendingRemoteDeleteResolutionsAsync(root, plan.Decision.CommandId));
        Assert.HasCount(0, await reopened.ReadPendingRemoteDeleteResolutionsAsync(RootId.New()));
        Assert.IsNull(await reopened.ReadRemoteDeleteResolutionForConflictAsync(Guid.NewGuid()));
        _ = await reopened.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        _ = await reopened.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
        _ = await reopened.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
        _ = await reopened.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
        var withoutReceipt = (await reopened.ReadPendingRemoteDeleteResolutionsAsync(root)).Single();
        Assert.IsNotNull(withoutReceipt.CompletedAt); Assert.IsNull(await reopened.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
        await reopened.RecordRemoteConflictActionReceiptAsync(new(plan.Decision, MirrorPulseRemoteConflictObservedStatus.Applied, null, plan.PreparedAt.AddSeconds(12)));
        Assert.HasCount(0, await reopened.ReadPendingRemoteDeleteResolutionsAsync(root));
        Assert.AreEqual(plan.Decision, (await reopened.ReadRemoteDeleteResolutionForConflictAsync(plan.Decision.Conflict.ConflictId))!.Plan.Decision);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("batch")]
    [DataRow("command")]
    public async Task DeleteRecoveryDiscoveryCannotPublishDamagedOwnershipAsAnExecutableCommand(string damaged)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0); _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        }
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "owner" => "UPDATE remote_delete_resolutions SET fingerprint=zeroblob(32);",
            "batch" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_member_retirement_batches;",
            _ => "UPDATE remote_conflict_actions SET fingerprint=zeroblob(32);"
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadRemoteDeleteResolutionForConflictAsync(plan.Decision.Conflict.ConflictId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadPendingRemoteDeleteResolutionsAsync(plan.Entries[0].Original.Context.Member.RootId!.Value));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(plan.Decision.CommandId))!.State);
    }
}
