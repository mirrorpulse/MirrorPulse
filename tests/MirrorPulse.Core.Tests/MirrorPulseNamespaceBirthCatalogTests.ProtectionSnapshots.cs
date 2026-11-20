using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectionSnapshotKeepsFirstProvenanceAndReadsTheCompletedPositionAcrossOwners(bool original)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveCompletion completed;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            if (original) setup = await WithOriginalMovableFileAsync(first, setup);
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
            var before = await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId);
            Assert.IsNotNull(before);
            Assert.AreEqual(setup.Member.RelativePath, before.Context.Location.RelativePath);
            Assert.IsNull(await first.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
            // Retained facts remain readable under a pending root fence without granting a move.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId));
            completed = await CompleteMoveChainStepAsync(first, setup.Move);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var snapshot = await second.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(setup.Member, snapshot.Context.Member);
        Assert.AreEqual(completed.RelativePath, snapshot.Context.Location.RelativePath);
        Assert.AreEqual(completed.Child, snapshot.Context.PositionVerification);
        Assert.AreEqual(await second.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), snapshot.Identity);
        Assert.AreEqual(setup.Move.ProtectionId, snapshot.Context.Protection.ProtectionId);
        if (original)
        {
            Assert.IsNull(snapshot.Observation); Assert.IsNull(snapshot.BornParent);
            Assert.AreEqual(setup.Move.ProtectionId, snapshot.PermissionOperationId);
        }
        else
        {
            Assert.AreEqual(setup.Born.Observation, snapshot.Observation);
            Assert.AreEqual(new(setup.Member.OriginId, setup.Move.ProtectionId), snapshot.BornParent);
            Assert.AreEqual(Guid.Empty, snapshot.PermissionOperationId);
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("birth")]
    [DataRow("observation")]
    [DataRow("protection")]
    [DataRow("snapshot")]
    public async Task ProtectionReadTransactionCannotHideCorruptParentMoveHistoryInALaterRead(string reader)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareFirstPlacedChildAsync(catalog);
        await catalog.RegisterNamespaceMemberAsync(setup.Member);
        Assert.IsNotNull(await ReadPlacedBirthHistoryAsync(catalog, setup, reader));
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_directory_move_completions SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadPlacedBirthHistoryAsync(catalog, setup, reader));
    }

    [TestMethod]
    public async Task LaterProtectionSnapshotReadsTheNewEpochWithoutChangingFirstMembership()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        var first = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Move.Member.MemberId);
        Assert.IsNotNull(first);
        var refresh = await LocatedBornEpochAsync(catalog, setup.Move.Member, setup.Completion.Root.VerifiedAt.AddSeconds(1));
        await catalog.RecordNamespaceBirthProtectionAsync(refresh);
        var second = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Move.Member.MemberId);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first.Context.Protection.ProtectionId, second.Context.Protection.ProtectionId);
        Assert.AreEqual(refresh.ProtectionId, second.Context.Protection.ProtectionId);
        Assert.AreEqual(first.Context.Member, second.Context.Member);
        Assert.AreEqual(first.Observation, second.Observation);
        Assert.AreEqual(first.Context.Location, second.Context.Location);
    }

    [TestMethod]
    public async Task MissingMemberSnapshotDoesNotAdoptAnUnregisteredObject()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(Guid.Empty));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static async Task<object?> ReadPlacedBirthHistoryAsync(MirrorPulseProductCatalog catalog, PlacedChildFixture setup, string reader) =>
        reader switch
        {
            "birth" => await catalog.ReadNamespaceBirthAsync(setup.Born.Birth.OperationId),
            "observation" => await catalog.ReadNamespaceBirthObservationAsync(setup.Born.Birth.OperationId),
            "protection" => await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Born.Birth.OperationId),
            _ => await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId)
        };
}
