using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelRecoveryAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnsealedRootLabelPendingDiscoveryRetainsItsOriginalOwnerAcrossCatalogOwners(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            AssertSinglePendingUnsealedLabel(await catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId), setup.Move.RootId, setup.Move.OperationId);
            Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync());
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        AssertSinglePendingUnsealedLabel(await reopened.ReadNamespacePendingOperationsAsync(setup.Move.RootId), setup.Move.RootId, setup.Move.OperationId);
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsEmpty(await reopened.ReadNamespacePendingOperationsAsync(RootId.New()));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelRecoveryAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelPendingDiscoveryKeepsOneExistingSealedMoveReference(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { new MirrorPulseNamespacePendingOperation(setup.Move.OperationId, setup.Move.RootId, MirrorPulseNamespacePendingOperationKind.Move) },
            (await reopened.ReadNamespacePendingOperationsAsync(setup.Move.RootId)).ToArray());
        Assert.AreEqual(setup.Move, await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelRecoveryAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TerminalUnsealedCancellationDiscoveryRetainsALaterOriginalLabelAndItsFence(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseRootLabelUnsealedCancellation cancelled; Guid later;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            var entries = tree.Members.Select(snapshot =>
            {
                var context = snapshot.Context; var member = context.Member; var location = context.Location;
                return new MirrorPulseNamespaceDirectoryMoveEntry(member.MemberId, location.ParentMemberId, location.RelativePath, member.IsDirectory,
                    member.LocalObject, member.OwnerSid, context.Protection.ProtectionId, context.Protection.VerifiedAt,
                    location.CompletedMoveOperationId, snapshot.Identity, location.RelativePath.Count(character => character == '/'))
                { PreviousPositionOperationId = location.PositionOperationId };
            }).ToArray();
            var observation = RootCancellationFromTree(setup, tree);
            cancelled = new(1, setup.Root.MemberId, observation, new(1, setup.Move.OperationId, observation.PublicEvidenceSha256, entries));
            _ = await catalog.CancelUnsealedRootLabelAsync(cancelled);
            Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var next = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Later", "Docs",
                setup.Rename.Proof! with { CapturedAt = cancelled.Observation.VerifiedAt.AddSeconds(1) });
            later = next.Intent.OperationId;
            AssertSinglePendingUnsealedLabel(await catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId), setup.Move.RootId, later);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        _ = await reopened.CancelUnsealedRootLabelAsync(cancelled);
        AssertSinglePendingUnsealedLabel(await reopened.ReadNamespacePendingOperationsAsync(setup.Move.RootId), setup.Move.RootId, later);
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.AreEqual(later, (await reopened.ReadManagedRootRenameAsync(setup.Move.RootId))!.OperationId);
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(later));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
    }

    [TestMethod]
    [TestCategory("RootLabelRecoveryAdmission")]
    [DataRow("payload")]
    [DataRow("root-owner")]
    public async Task PendingRootLabelDiscoveryRefusesDamagedCurrentOwnershipWithoutInventingReadiness(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        string sql = damage == "payload" ? "UPDATE managed_root_renames SET payload='{}';" :
            $"UPDATE managed_root_renames SET root_id='{Guid.NewGuid():D}';";
        await ExecuteSqlAsync(fixture.Paths, sql);
        await RequireCancellationAdmissionRefusalAsync(() => catalog.ReadNamespacePendingOperationsAsync(setup.Move.RootId));
        await RequireCancellationAdmissionRefusalAsync(() => catalog.ReadNamespacePendingOperationsAsync(RootId.New()));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelRecoveryAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelledSealedLabelFenceMetadataCanPrecedeCurrentRoleRecoveryWithoutGrantingIt(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseRootLabelCancellation cancelled;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Move.PreparedAt.AddSeconds(-1));
            cancelled = RootCancellationFromTree(setup, await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId));
            _ = await catalog.CancelUnstartedRootLabelAsync(cancelled);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsFalse((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        var next = await reopened.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Later", "Docs",
            setup.Rename.Proof! with { CapturedAt = cancelled.VerifiedAt.AddSeconds(1) });
        _ = await reopened.CancelUnstartedRootLabelAsync(cancelled);
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        AssertSinglePendingUnsealedLabel(await reopened.ReadNamespacePendingOperationsAsync(setup.Move.RootId), setup.Move.RootId, next.Intent.OperationId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static void AssertSinglePendingUnsealedLabel(IReadOnlyList<MirrorPulseNamespacePendingOperation> pending, RootId root, Guid operation)
    {
        Assert.HasCount(1, pending); Assert.AreEqual(operation, pending[0].OperationId); Assert.AreEqual(root, pending[0].RootId);
        Assert.AreEqual("RootLabel", pending[0].Kind.ToString());
    }
}
