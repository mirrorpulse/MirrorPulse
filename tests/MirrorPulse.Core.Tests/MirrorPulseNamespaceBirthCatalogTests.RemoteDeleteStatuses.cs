using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("Prepared")]
    [DataRow("InvocationMayHaveBegun")]
    [DataRow("SettlingMembers")]
    [DataRow("Partial")]
    [DataRow("CommandReceiptPending")]
    [DataRow("Completed")]
    public async Task ExplicitDeleteStatusPreservesUnknownOutcomeAndSeparateMemberAndCommandCompletionAfterReopen(string stage)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDeleteDecisionFixtureAsync(catalog, 1); _ = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            if (stage != "Prepared") _ = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            if (stage is not ("Prepared" or "InvocationMayHaveBegun"))
                _ = await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
            if (stage is "Partial" or "CommandReceiptPending" or "Completed")
                _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[1])]);
            if (stage is "CommandReceiptPending" or "Completed")
            {
                _ = await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
                _ = await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
            }
            if (stage == "Completed") await catalog.RecordRemoteConflictActionReceiptAsync(new(plan.Decision,
                MirrorPulseRemoteConflictObservedStatus.Applied, null, plan.PreparedAt.AddSeconds(12)));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadRemoteDeleteResolutionStatusAsync(plan.Decision.CommandId))!;
        Assert.AreEqual(stage == "Partial" ? "SettlingMembers" : stage, actual.Stage);
        Assert.AreEqual(plan.Decision.CommandId, actual.CommandId); Assert.AreEqual(plan.Decision.Conflict.ConflictId, actual.ConflictId);
        Assert.AreEqual(2, actual.MemberCount); Assert.AreEqual(stage == "Partial" ? 1 : stage is "CommandReceiptPending" or "Completed" ? 2 : 0, actual.SettledMembers);
        Assert.AreEqual(stage == "Completed", actual.CommandCompleted);
        Assert.AreEqual(stage is "CommandReceiptPending" or "Completed", actual.MembersCompletedAt is not null);
        Assert.AreEqual(stage is not ("Prepared" or "InvocationMayHaveBegun"), actual.Outcome is not null);
        Assert.IsNull(await reopened.ReadRemoteDeleteResolutionStatusAsync(Guid.NewGuid()));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
