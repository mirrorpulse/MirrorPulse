using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    [DataRow("payload")]
    [DataRow("missing-owner")]
    [DataRow("missing-tree")]
    public async Task DamagedTerminalCancellationCannotSupplyReadinessOrAReadableOriginal(string damage)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            _ = await catalog.CancelUnstartedRootLabelAsync(await RootCancellationAsync(catalog, setup));
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "payload" => "UPDATE root_label_unstarted_cancellations SET payload=x'00';",
            "missing-owner" => "DELETE FROM root_label_unstarted_cancellations;",
            "missing-tree" => "DELETE FROM namespace_directory_move_snapshots;",
            _ => throw new AssertFailedException("Unknown damage.")
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, (await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent.Phase);
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    [DataRow("table")]
    [DataRow("index")]
    [DataRow("index-shape")]
    [DataRow("downgraded-owner")]
    public async Task Schema91NeverRecreatesLostOrReinterpretsOlderCancellationOwnership(string damage)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRootSubtreeFixtureAsync(catalog);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            _ = await catalog.CancelUnstartedRootLabelAsync(await RootCancellationAsync(catalog, setup));
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "table" => "DROP TABLE root_label_unstarted_cancellations;",
            "index" => "DROP INDEX root_label_unstarted_cancellations_root;",
            "index-shape" => "DROP INDEX root_label_unstarted_cancellations_root; CREATE INDEX root_label_unstarted_cancellations_root ON root_label_unstarted_cancellations(operation_id,root_id);",
            "downgraded-owner" => "PRAGMA user_version=90;",
            _ => throw new AssertFailedException("Unknown damage.")
        });
        MirrorPulseProductCatalog? unexpected = null;
        try { await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => unexpected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)); }
        finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    public async Task CleanSchema90MigrationPreservesEveryOriginalPendingLabelAndItsPublicProof()
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE root_label_unstarted_cancellations; PRAGMA user_version=90;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        CollectionAssert.Contains((await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
        Assert.IsNull(await reopened.ReadRootLabelCancellationAsync(setup.Move.OperationId));
        _ = await reopened.CancelUnstartedRootLabelAsync(await RootCancellationAsync(reopened, setup));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    public async Task CompletedCancellationCannotAdoptAChangedLaterTerminalRecord()
    {
        using var fixture = new Fixture(); MirrorPulseRootLabelCancellation original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRootSubtreeFixtureAsync(catalog);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            original = await RootCancellationAsync(catalog, setup);
            _ = await catalog.CancelUnstartedRootLabelAsync(original);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.CancelUnstartedRootLabelAsync(original with { VerifiedAt = original.VerifiedAt.AddSeconds(1) }));
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await reopened.ReadRootLabelCancellationAsync(original.OperationId)));
    }
}
