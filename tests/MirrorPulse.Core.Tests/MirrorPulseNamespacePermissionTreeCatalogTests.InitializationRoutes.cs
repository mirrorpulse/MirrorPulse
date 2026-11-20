using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    public async Task PendingInitializationRetainsItsChosenRouteAcrossRestartAndCannotChangeIt()
    {
        using var fixture = new CatalogFixture();
        var original = Initialization(RootId.New()) with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory };
        var current = Initialization(RootId.New()) with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        var legacy = Initialization(RootId.New());
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            foreach (var intent in new[] { original, current, legacy }) await catalog.PrepareNamespaceInitializationAsync(intent);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceInitializationAsync(
                original with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceInitializationAsync(
                legacy with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory }));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var pending = await reopened.ReadPendingNamespaceInitializationsAsync(); Assert.HasCount(3, pending);
        Assert.AreEqual(original.RequestedInventoryKind, pending.Single(item => item.Intent.OperationId == original.OperationId).Intent.RequestedInventoryKind);
        Assert.AreEqual(current.RequestedInventoryKind, pending.Single(item => item.Intent.OperationId == current.OperationId).Intent.RequestedInventoryKind);
        Assert.IsNull(pending.Single(item => item.Intent.OperationId == legacy.OperationId).Intent.RequestedInventoryKind);
        Assert.IsTrue(pending.All(item => item.Completion is null));
        CollectionAssert.AreEquivalent(new[] { original.Roots[0].RootId, current.Roots[0].RootId, legacy.Roots[0].RootId },
            (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task PendingDiscoveryExcludesHistoricalCompletionsAndPreservesANewerRootOwner()
    {
        using var fixture = new CatalogFixture(); var (definition, captures) = Tree();
        var intent = Initialization(definition.Anchor.Intent.RootId!.Value) with
        { Roots = [new(definition.Anchor.Intent.RootId.Value, definition.ManifestId)], RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespaceInitializationAsync(intent);
        var snapshots = await RegisterInitializationOriginalsAsync(catalog, definition, captures);
        await catalog.CompleteNamespaceInitializationAsync(intent.OperationId, snapshots, AuditTime(definition));
        Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync());
        var next = Initialization(definition.Anchor.Intent.RootId.Value) with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        await catalog.PrepareNamespaceInitializationAsync(next);
        Assert.AreEqual(next.OperationId, (await catalog.ReadPendingNamespaceInitializationsAsync()).Single().Intent.OperationId);
        Assert.HasCount(2, await catalog.ReadNamespaceInitializationsAsync());
        CollectionAssert.AreEqual(new[] { definition.Anchor.Intent.RootId.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("missing-reservation")]
    [DataRow("orphan-reservation")]
    [DataRow("missing-intent")]
    public async Task PendingDiscoveryDoesNotHideDamagedInitializationOwnership(string damage)
    {
        using var fixture = new CatalogFixture(); var intent = Initialization(RootId.New());
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) await catalog.PrepareNamespaceInitializationAsync(intent);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "missing-reservation" => "DELETE FROM namespace_initialization_roots;",
            "missing-intent" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_initialization_intents;",
            _ => $"PRAGMA foreign_keys=OFF; INSERT INTO namespace_initialization_roots(root_id,operation_id,manifest_id) VALUES('{RootId.New().Value:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}');"
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadPendingNamespaceInitializationsAsync());
    }

    [TestMethod]
    public async Task WrongPendingRouteCannotCompleteBeforeAnyNativeInventoryWork()
    {
        using var fixture = new CatalogFixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = Initialization(RootId.New()) with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory };
        var current = Initialization(RootId.New()) with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        await catalog.PrepareNamespaceInitializationAsync(original); await catalog.PrepareNamespaceInitializationAsync(current);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(original.OperationId,
            new(Guid.NewGuid(), [], [], [], string.Empty), DateTimeOffset.UtcNow));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceInitializationAsync(current.OperationId,
            new MirrorPulseNamespaceMemberProtectionSnapshot[2], DateTimeOffset.UtcNow));
        Assert.HasCount(2, await catalog.ReadPendingNamespaceInitializationsAsync());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceInitializationAsync(
            Initialization(RootId.New()) with { RequestedInventoryKind = (MirrorPulseNamespaceInitializationInventoryKind)254 }));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
