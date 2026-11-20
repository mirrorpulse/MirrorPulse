using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    public async Task InitializationDiscoveryPagesPendingAndHistoricalAttemptsAcrossRestartWithoutReleasingFences()
    {
        using var fixture = new CatalogFixture();
        var (definition, captures) = Tree();
        var first = Initialization(RootId.New()) with { OperationId = Guid.Parse("10000000-0000-4000-8000-000000000000") };
        var second = Initialization(definition.Anchor.Intent.RootId!.Value) with
        {
            OperationId = Guid.Parse("20000000-0000-4000-8000-000000000000"),
            Roots = [new(definition.Anchor.Intent.RootId.Value, definition.ManifestId)]
        };
        var third = Initialization(RootId.New()) with { OperationId = Guid.Parse("30000000-0000-4000-8000-000000000000") };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            // Preparation order does not determine the stable cursor order.
            await catalog.PrepareNamespaceInitializationAsync(third);
            await catalog.PrepareNamespaceInitializationAsync(second);
            await catalog.PrepareNamespaceInitializationAsync(first);
            var snapshots = await RegisterInitializationOriginalsAsync(catalog, definition, captures);
            await catalog.CompleteNamespaceInitializationAsync(second.OperationId, snapshots, AuditTime(definition));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var page = await reopened.ReadNamespaceInitializationsAsync(limit: 1);
        Assert.AreEqual(first.OperationId, page.Single().Intent.OperationId); Assert.IsNull(page.Single().Completion);
        CollectionAssert.AreEqual(first.Roots.ToArray(), page.Single().Intent.Roots.ToArray());
        page = await reopened.ReadNamespaceInitializationsAsync(page[^1].Intent.OperationId, 1);
        Assert.AreEqual(second.OperationId, page.Single().Intent.OperationId); Assert.AreEqual(4, page.Single().Completion!.MemberCount);
        page = await reopened.ReadNamespaceInitializationsAsync(page[^1].Intent.OperationId, 1);
        Assert.AreEqual(third.OperationId, page.Single().Intent.OperationId); Assert.IsNull(page.Single().Completion);
        Assert.IsEmpty(await reopened.ReadNamespaceInitializationsAsync(page[^1].Intent.OperationId, 1));
        CollectionAssert.AreEqual(new[] { first.OperationId, second.OperationId, third.OperationId },
            (await reopened.ReadNamespaceInitializationsAsync()).Select(status => status.Intent.OperationId).ToArray());
        Assert.AreEqual(second.OperationId, (await reopened.ReadNamespaceInitializationsAsync(
            Guid.Parse("15000000-0000-4000-8000-000000000000"), 1)).Single().Intent.OperationId);
        CollectionAssert.AreEquivalent(new[] { first.Roots[0].RootId, third.Roots[0].RootId },
            (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("lost-admission")]
    [DataRow("lost-reservation")]
    [DataRow("changed-payload")]
    [DataRow("orphan-completion")]
    [DataRow("invalid-index")]
    public async Task InitializationDiscoveryRejectsDamagedRecoveryIndexesInsteadOfReturningAnEmptyPage(string damage)
    {
        using var fixture = new CatalogFixture(); var intent = Initialization(RootId.New());
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareNamespaceInitializationAsync(intent);
        string mutation = damage switch
        {
            "lost-admission" => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_initialization_intents;",
            "lost-reservation" => "DELETE FROM namespace_initialization_roots;",
            "changed-payload" => "UPDATE namespace_initialization_intents SET payload=X'00';",
            "orphan-completion" => $"PRAGMA foreign_keys=OFF; INSERT INTO namespace_initialization_completions(operation_id,payload,fingerprint) VALUES('{Guid.NewGuid():D}',X'00',zeroblob(32));",
            _ => "INSERT INTO namespace_initialization_intents(operation_id,payload,fingerprint) VALUES('invalid',X'00',zeroblob(32));"
        };
        await ExecuteSqlAsync(fixture.Paths, mutation);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceInitializationsAsync());
    }

    [TestMethod]
    public async Task InitializationDiscoveryRejectsUnboundedPagesAndEmptyCursors()
    {
        using var fixture = new CatalogFixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceInitializationsAsync(Guid.Empty));
        foreach (int limit in new[] { -1, 0, 257, int.MaxValue })
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => catalog.ReadNamespaceInitializationsAsync(limit: limit));
        Assert.IsEmpty(await catalog.ReadNamespaceInitializationsAsync());
    }
}
