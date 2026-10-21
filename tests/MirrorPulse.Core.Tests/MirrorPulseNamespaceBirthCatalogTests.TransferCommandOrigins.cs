using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlledTransferRetainsItsOriginalRequestAcrossOwnersWithoutInventingJournalAcknowledgement(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog, sameInstance)).Plan);
            var prepared = await catalog.PrepareNamespaceTransferAsync(plan);
            Assert.AreEqual(plan, prepared.Plan);
            await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferAsync(plan.OperationId))!;
        Assert.AreEqual(plan, actual.Plan); Assert.AreEqual(plan.CommandOrigin, actual.Plan.CommandOrigin);
        Assert.IsNotNull(plan.CommandOrigin);
        Assert.AreEqual(plan.OperationId, actual.Plan.CommandOrigin!.CommandId);
        Assert.AreNotEqual(plan.CopyOperationId, actual.Plan.CommandOrigin.CommandId);
        Assert.AreNotEqual(plan.DeleteOperationId, actual.Plan.CommandOrigin.CommandId);
        Assert.AreEqual(await reopened.PrepareNamespaceTransferAsync(plan), actual);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceTransferAsync(plan with
        { CommandOrigin = plan.CommandOrigin with { RequestedAt = plan.CommandOrigin.RequestedAt.AddSeconds(-1) } }));
        Assert.IsNull(await reopened.ReadMutationAsync(plan.OperationId));
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        var status = (await reopened.ReadNamespaceTransferStatusAsync(plan.OperationId))!;
        Assert.IsFalse(status.JournalAcknowledged); Assert.IsFalse(status.Completed);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("version")]
    [DataRow("command-id")]
    [DataRow("source")]
    [DataRow("parent")]
    [DataRow("name")]
    [DataRow("future")]
    [DataRow("no-time")]
    [DataRow("legacy-version")]
    public async Task TransferCannotAdoptASubstitutedOrUnownedControlledRequest(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan); var command = original.CommandOrigin!;
        var invalid = failure switch
        {
            "missing" => original with { CommandOrigin = null },
            "version" => original with { CommandOrigin = command with { Version = 2 } },
            "command-id" => original with { CommandOrigin = command with { CommandId = original.CopyOperationId } },
            "source" => original with { CommandOrigin = command with { SourceMemberId = original.TargetParent.Context.Member.MemberId } },
            "parent" => original with { CommandOrigin = command with { TargetParentMemberId = original.SourceParent.Context.Member.MemberId } },
            "name" => original with { CommandOrigin = command with { TargetName = "other.txt" } },
            "future" => original with { CommandOrigin = command with { RequestedAt = original.PreparedAt.AddSeconds(1) } },
            "no-time" => original with { CommandOrigin = command with { RequestedAt = default } },
            _ => original with { Version = 1 }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(original.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task LegacyTransferReopensItsOriginalCanonicalPlanWithoutBackfillingACommandOrigin()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan legacy; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            legacy = (await PrepareTransferFixtureAsync(catalog)).Plan; original = JsonSerializer.Serialize(legacy);
            Assert.IsFalse(original.Contains("CommandOrigin", StringComparison.Ordinal));
            await catalog.PrepareNamespaceTransferAsync(legacy);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferAsync(legacy.OperationId))!;
        Assert.AreEqual(1, actual.Plan.Version); Assert.IsNull(actual.Plan.CommandOrigin); Assert.AreEqual(original, JsonSerializer.Serialize(actual.Plan));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceTransferAsync(WithTransferCommandOrigin(legacy)));
        Assert.AreEqual(await reopened.ReadNamespaceTransferAsync(legacy.OperationId), actual);
    }

    private static MirrorPulseNamespaceTransferPlan WithTransferCommandOrigin(MirrorPulseNamespaceTransferPlan plan) => plan with
    {
        Version = 2,
        CommandOrigin = new(1, plan.OperationId, plan.Source.Context.Member.MemberId, plan.TargetParent.Context.Member.MemberId,
            plan.TargetRelativePath[(plan.TargetRelativePath.LastIndexOf('/') + 1)..], plan.PreparedAt)
    };
}
