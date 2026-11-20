using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CapturedLabelExecutionRetainsItsExactRootCaptureAndCompletesOnlyItsOriginalSubtree(bool enabled)
    {
        // Actual catalog transactions with synthetic permission observations; native recovery is tested separately.
        using var fixture = new Fixture();
        RootSubtreeFixture setup; MirrorPulseNamespaceInitializationIntent initializer;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled, captureOriginalRoot: true);
            var selected = await ReadCapturedLabelRootAsync(catalog, setup);
            initializer = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId,
                Guid.NewGuid(), setup.Move.PreparedAt.AddSeconds(1), selected))!;
            Assert.AreEqual(selected, initializer.Roots.Single());
            Assert.IsNull(initializer.Roots[0].BornRootMemberId);
            Assert.AreEqual(setup.Root.MemberId, initializer.RootLabelCancellation!.RootMemberId);
            var plan = (await catalog.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId))!;
            CollectionAssert.AreEqual(setup.Members.Select(member => member.MemberId).ToArray(), plan.Members.ToArray());
            CollectionAssert.AreEqual(setup.Members.Skip(1).Select(member => member.MemberId).ToArray(), plan.BornMembers.ToArray());
            Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(
                initializer.OperationId, inventory, initializer.PreparedAt.AddSeconds(1)));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retry = await reopened.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId,
            Guid.NewGuid(), initializer.PreparedAt.AddSeconds(1), initializer.Roots[0]);
        Assert.AreEqual(JsonSerializer.Serialize(initializer), JsonSerializer.Serialize(retry));
        setup = setup with { Move = setup.Move with { PreparedAt = initializer.PreparedAt.AddSeconds(1) } };
        var receipt = await PrepareRootSubtreeFactsAsync(reopened, setup);
        await reopened.CompleteNamespaceDirectoryMoveAsync(receipt);
        var after = await reopened.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
        var completion = await reopened.CompleteNamespaceCurrentInitializationAsync(initializer.OperationId,
            after, receipt.Root.VerifiedAt.AddSeconds(1));
        Assert.AreEqual(setup.Move.RootId, completion.ScopeRoot);
        Assert.AreEqual(setup.Rename.Proof, (await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Proof);
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow("other-root")]
    [DataRow("missing-capture")]
    [DataRow("born-substitution")]
    public async Task CapturedLabelExecutionCannotBorrowAnotherRootOrMissingProvenance(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, captureOriginalRoot: true);
        var selected = await ReadCapturedLabelRootAsync(catalog, setup);
        selected = damage switch
        {
            "other-root" => selected with { RootId = RootId.New() },
            "missing-capture" => selected with { ManifestId = Guid.NewGuid() },
            "born-substitution" => new(setup.Move.RootId, Guid.Empty) { BornRootMemberId = setup.Root.MemberId },
            _ => selected
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRootLabelCancellationInitializationAsync(
            setup.Move.OperationId, Guid.NewGuid(), setup.Move.PreparedAt.AddSeconds(1), selected));
        Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync());
        Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    private static async Task<MirrorPulseNamespaceInitializationRoot> ReadCapturedLabelRootAsync(
        MirrorPulseProductCatalog catalog, RootSubtreeFixture setup)
    {
        var tree = (await catalog.ReadNamespacePermissionTreesAsync(setup.Move.RootId)).Single();
        return new(setup.Move.RootId, tree.Definition.ManifestId);
    }
}
