using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ExplicitDeleteSelectsDistinctSuccessorAcrossOwnersWithoutRewritingOriginalConflictOrDecision()
    {
        using var fixture = new Fixture(); RootRegistration root; CloudPlaceholderIdentity successor;
        MirrorPulseRemoteDeleteResolutionPlan plan; string originals; string decision; MirrorPulseRemoteConflictActionReceipt receipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog); root = setup.Root;
            plan = setup.Plan with { Version = 2, RootKey = root.UniquenessKey };
            await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
            await CompleteExplicitGenerationDecisionAsync(catalog, plan);
            var retirement = (await catalog.ReadNamespaceControlledRetirementGenerationsAsync(plan.Entries[0].Original.Identity.ItemId!.Value)).Single();
            Assert.IsNotNull(retirement.ExplicitSourceNameEpoch); Assert.IsGreaterThanOrEqualTo(2L, retirement.ExplicitSourceNameEpoch.Value);
            originals = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
            decision = JsonSerializer.Serialize(await catalog.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId));
            receipt = (await catalog.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId))!;
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var resolver = new MirrorPulseRemoteIdentityResolver(reopened, new TransferClock(plan.PreparedAt.AddSeconds(20)));
            successor = await resolver.ResolveAsync(root, "backend-file", "later-v3");
            var old = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "backend-file", "accepted").ToCfSharp();
            Assert.AreNotEqual(old.ItemId, successor.ItemId); Assert.AreNotEqual(old.RemoteId, successor.RemoteId);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(root, old).AsTask());
            await resolver.RequireCurrentAsync(root, successor);
            var selected = (await reopened.ReadRemoteIdentityGenerationAsync(root.RootId, old.ItemId))!;
            Assert.AreEqual(3, selected.Version); Assert.AreEqual(Guid.Empty, selected.TransferOperationId);
            Assert.AreEqual(plan.Decision.CommandId, (await reopened.ReadNamespaceRetirementAsync(selected.RetirementOperationId))!.Intent.BatchOperationId);
        }
        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var restored = await new MirrorPulseRemoteIdentityResolver(third).ResolveAsync(root, "backend-file", "later-v4");
        Assert.AreEqual(successor.ItemId, restored.ItemId); Assert.AreEqual(successor.RemoteId, restored.RemoteId);
        Assert.AreEqual("later-v4", restored.RemoteRevision);
        Assert.AreEqual(originals, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.AreEqual(decision, JsonSerializer.Serialize(await third.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId)));
        Assert.AreEqual(receipt, await third.ReadRemoteConflictActionReceiptAsync(plan.Decision.CommandId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("started")]
    [DataRow("outcome")]
    [DataRow("member")]
    [DataRow("completed-without-receipt")]
    public async Task ExplicitDeleteCannotSelectBeforeEveryOriginalSettlementAndCommandReceipt(string boundary)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog);
        var plan = setup.Plan with { Version = 2, RootKey = setup.Root.UniquenessKey };
        await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        if (boundary != "started") await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
        if (boundary is "member" or "completed-without-receipt")
            await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId, [DecisionMember(plan, plan.Entries[0])]);
        if (boundary == "completed-without-receipt")
            await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(plan.PreparedAt.AddSeconds(20)));
        var old = await resolver.ReadCurrentAsync(setup.Root, "backend-file");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(setup.Root, old).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resolver.ResolveAsync(setup.Root, "backend-file", "later-v3").AsTask());
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(setup.Root.RootId, old.ItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("root")]
    [DataRow("root-key")]
    [DataRow("previous-remote")]
    [DataRow("retirement")]
    [DataRow("name-epoch")]
    [DataRow("before-receipt")]
    [DataRow("automatic-kind")]
    public async Task ExplicitDeleteSuccessorRejectsForeignOriginalsEpochOrLifecycleKind(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog);
        var plan = setup.Plan with { Version = 2, RootKey = setup.Root.UniquenessKey };
        await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        await CompleteExplicitGenerationDecisionAsync(catalog, plan);
        var retirement = (await catalog.ReadNamespaceControlledRetirementGenerationsAsync(plan.Entries[0].Original.Identity.ItemId!.Value)).Single();
        var identity = plan.Entries[0].Original.Identity;
        var selection = new MirrorPulseRemoteIdentityGeneration(3, setup.Root.RootId, setup.Root.InstanceId, setup.Root.UniquenessKey,
            identity.ItemId!.Value, identity.RemoteId!, null, Guid.NewGuid(), identity.ItemId.Value, identity.RemoteId!, Guid.NewGuid(),
            "opaque-successor", Guid.Empty, retirement.OperationId, retirement.ExplicitSourceNameEpoch!.Value, plan.PreparedAt.AddSeconds(20));
        var wrong = mismatch switch
        {
            "instance" => selection with { InstanceId = MirrorPulse.Core.Contracts.InstanceId.New() },
            "root" => selection with { RootId = MirrorPulse.Core.Contracts.RootId.New() },
            "root-key" => selection with { RootKey = "foreign" },
            "previous-remote" => selection with { BaseRemoteId = "foreign", PreviousRemoteId = "foreign" },
            "retirement" => selection with { RetirementOperationId = Guid.NewGuid() },
            "name-epoch" => selection with { RetiredNameEpoch = selection.RetiredNameEpoch + 1 },
            "before-receipt" => selection with { SelectedAt = plan.PreparedAt.AddSeconds(11) },
            _ => selection with { Version = 2 }
        };
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(wrong));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.AreEqual(selection, await catalog.SelectRemoteIdentityGenerationAsync(selection));
    }
}
