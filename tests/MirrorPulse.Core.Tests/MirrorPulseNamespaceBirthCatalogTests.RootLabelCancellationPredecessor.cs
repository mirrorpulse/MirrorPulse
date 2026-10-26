using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationPreservesARealPredecessorClaimOnRollbackAndReleasesOnlyItsOwnClaimOnCommit(bool failFinalWrite)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        var first = await PrepareRootSubtreeFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(first);
        // Synthetic catalog records establish an actual non-null completed-move
        // predecessor; no native movement or SDK reconciliation is asserted here.
        var label = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Later", "Renamed",
            setup.Rename.Proof! with { CapturedAt = first.Root.VerifiedAt.AddSeconds(1) });
        var second = setup.Move with
        {
            OperationId = label.Intent.OperationId,
            Source = setup.Move.Source with { RelativePath = "Renamed" },
            Target = setup.Move.Target with { RelativePath = "Later" },
            PreviousMoveOperationId = first.OperationId,
            SourcePositionOperationId = first.OperationId,
            PreparedAt = label.Proof!.CapturedAt.AddSeconds(1)
        };
        await catalog.PrepareNamespaceMemberMoveAsync(second);
        var current = setup with
        {
            Registration = (await catalog.ReadAdapterTopologyAsync()).Roots.Single(),
            Rename = label,
            Move = second
        };
        var cancellation = await RootCancellationAsync(catalog, current);
        if (failFinalWrite)
        {
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_predecessor_cancel BEFORE UPDATE ON managed_root_rename_history BEGIN SELECT RAISE(ABORT,'injected predecessor cancellation failure'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CancelUnstartedRootLabelAsync(cancellation));
            // Reading the pending move validates its exclusive predecessor claim
            // and reserved name, rather than merely checking a pending-row count.
            Assert.AreEqual(second, await catalog.ReadPendingNamespaceMemberMoveAsync(second.RootId));
            Assert.AreEqual(label.Intent, (await catalog.ReadManagedRootRenameHistoryAsync(second.OperationId))!.Intent);
            Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(second.OperationId));
            await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_predecessor_cancel;");
        }
        _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(second.RootId));
        var thirdLabel = await catalog.PrepareManagedRootRenameWithProofAsync(second.RootId, "Later", "Renamed",
            label.Proof! with { CapturedAt = cancellation.VerifiedAt.AddSeconds(1) });
        var third = second with { OperationId = thirdLabel.Intent.OperationId, PreparedAt = cancellation.VerifiedAt.AddSeconds(2) };
        await catalog.PrepareNamespaceMemberMoveAsync(third);
        Assert.AreEqual(third, await catalog.ReadPendingNamespaceMemberMoveAsync(third.RootId));
        Assert.AreEqual(second, await catalog.ReadNamespaceMemberMoveAsync(second.OperationId));
        _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
        Assert.AreEqual(third, await catalog.ReadPendingNamespaceMemberMoveAsync(third.RootId));
        Assert.AreEqual(thirdLabel.Intent, (await catalog.ReadManagedRootRenameHistoryAsync(third.OperationId))!.Intent);
        Assert.AreEqual("Renamed", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
    }
}
