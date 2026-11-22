using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelSelectedBornInventory")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SelectedLabelRecoveryRetainsReadOnlyBornIdsInDirectParentFirstOrderWithoutSettlingItsOwner(bool enabled)
    {
        // Private SQLite metadata only; neither ordering nor membership grants native authority.
        using var fixture = new Fixture(); RootSubtreeFixture setup; Guid initializer;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var before = await reopened.ReadNamespaceInitializationStatusAsync(initializer);
        var plan = await reopened.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId);
        Assert.IsNotNull(plan); Assert.AreEqual(setup.Root.MemberId, plan.RootMemberId);
        Assert.AreEqual(initializer, plan.Owner.InitializationOperationId); Assert.HasCount(0, plan.PendingOriginals);
        var property = plan.GetType().GetProperty("BornMembers");
        Assert.IsNotNull(property, "The selected-root recovery must expose its validated born members separately from unresolved original permissions.");
        var value = property.GetValue(plan); Assert.IsInstanceOfType<IReadOnlyList<Guid>>(value);
        var born = (IReadOnlyList<Guid>)value;
        CollectionAssert.AreEqual(new[] { setup.Members[1].MemberId, setup.Members[2].MemberId }, born.ToArray());
        Assert.IsInstanceOfType<IList<Guid>>(born);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<Guid>)born).Clear());
        var again = await reopened.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId);
        CollectionAssert.AreEqual(born.ToArray(), ((IReadOnlyList<Guid>)property.GetValue(again)!).ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await reopened.ReadNamespaceInitializationStatusAsync(initializer)));
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
