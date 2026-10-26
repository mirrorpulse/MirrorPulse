using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task Schema89IndexesHistoricalExplicitStartsWithoutInventingRootKeyOrNameEpoch()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteResolutionPlan plan; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog); plan = setup.Plan;
            await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            await CompleteExplicitGenerationDecisionAsync(catalog, plan);
            original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX namespace_controlled_retirement_items; PRAGMA user_version=88;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var old = (await reopened.ReadNamespaceControlledRetirementGenerationsAsync(plan.Entries[0].Original.Identity.ItemId!.Value)).Single();
        Assert.IsNull(old.ExplicitSourceNameEpoch);
        var decision = (await reopened.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!;
        Assert.AreEqual(1, decision.Plan.Version); Assert.IsNull(decision.Plan.RootKey);
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(91L, await query.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Schema89RejectsMissingOrChangedControlledItemIndex(bool changed)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX namespace_controlled_retirement_items;" +
            (changed ? "CREATE INDEX namespace_controlled_retirement_items ON namespace_member_retirements(member_id);" : string.Empty));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        });
    }

    [TestMethod]
    public async Task ExplicitNameEpochRollsBackWithFailedOriginalMemberSettlement()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog);
        var plan = setup.Plan with { Version = 2, RootKey = setup.Root.UniquenessKey };
        await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, """
            CREATE TRIGGER fail_explicit_generation_member BEFORE INSERT ON remote_delete_resolution_members
            BEGIN SELECT RAISE(ABORT,'Synthetic settlement interruption'); END;
            """);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SettleRemoteDeleteResolutionPageAsync(
            plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.AreEqual(0, (await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId))!.NextOrdinal);
        var member = plan.Entries[0].Original.Context.Member;
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(plan.Parent.Context.Member.MemberId,
            Path.GetFileName(plan.Entries[0].Original.Context.Location.RelativePath)));
        var pending = (await catalog.ReadNamespaceControlledRetirementGenerationsAsync(plan.Entries[0].Original.Identity.ItemId!.Value)).Single();
        Assert.IsNull(pending.ExplicitSourceNameEpoch); Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Prepared, pending.Phase);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_explicit_generation_member;");
        await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
        var settled = (await catalog.ReadNamespaceControlledRetirementGenerationsAsync(plan.Entries[0].Original.Identity.ItemId!.Value)).Single();
        Assert.IsNotNull(settled.ExplicitSourceNameEpoch);
    }
}
