using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingRemoteUpdateRetainsANullDeclaredPredecessorWithoutInventingOne(bool conflict)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        plan = plan with { PreviousRemoteRevision = null };
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan); await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt);
        var terminal = await catalog.SettleNamespaceRemoteUpdateAsync(RemoteUpdateOutcome(plan, conflict, false));
        Assert.IsNull(terminal.Plan.PreviousRemoteRevision);
        Assert.AreEqual(plan.ChangeFingerprint, terminal.Plan.ChangeFingerprint);
        Assert.AreEqual(plan, (await catalog.ReadNamespaceRemoteUpdateAsync(plan.InstanceId, plan.BatchId, plan.Ordinal))!.Plan);
        Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task PendingRemoteUpdateDiscoveryRetainsOriginalRootAndEntryDigest()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
        var pending = (await catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId)).Single();
        Assert.AreEqual(plan.OperationId, pending.OperationId); Assert.AreEqual(MirrorPulseNamespacePendingOperationKind.RemoteUpdate, pending.Kind);
        Assert.AreEqual(plan, (await catalog.ReadPendingNamespaceRemoteUpdatesAsync(setup.Move.RootId)).Single().Plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRemoteUpdateAsync(plan with { ChangeFingerprint = new string('C', 64) }));
        Assert.AreEqual(plan, (await catalog.ReadPendingNamespaceRemoteUpdatesAsync(setup.Move.RootId)).Single().Plan);
    }
}
