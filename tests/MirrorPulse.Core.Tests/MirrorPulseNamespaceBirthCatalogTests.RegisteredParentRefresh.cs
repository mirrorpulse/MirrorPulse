using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task RegisteredDescendantKeepsItsOwnEpochAfterCurrentParentPlacementRefreshAcrossOwners(int descendantIndex)
    {
        using var fixture = new Fixture();
        DirectorySettlementFixture setup;
        MirrorPulseNamespaceBirthProtection rootEpoch, originalEpoch;
        MirrorPulseNamespaceBirthIntent firstBirth;
        MirrorPulseNamespaceBirthObservation firstObservation;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareDirectorySettlementAsync(first, movedChild: true);
            await first.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
            var descendant = setup.Members[descendantIndex];
            originalEpoch = (await first.ReadLatestNamespaceBirthProtectionAsync(descendant.OriginId))!;
            firstBirth = (await first.ReadNamespaceBirthAsync(descendant.OriginId))!;
            firstObservation = (await first.ReadNamespaceBirthObservationAsync(descendant.OriginId))!;
            rootEpoch = await LocatedBornEpochAsync(first, setup.Move.Member, setup.Completion.Root.VerifiedAt.AddSeconds(1));
            Assert.AreEqual(setup.Move.Born.Protection.RoleSid, rootEpoch.RoleSid);
            Assert.AreEqual(setup.Move.Born.Protection.Verification.Dacl, rootEpoch.Verification.Dacl);
            await first.RecordNamespaceBirthProtectionAsync(rootEpoch);
            Assert.AreEqual(originalEpoch.ProtectionId, (await first.ReadNamespaceMemberProtectionEpochAsync(descendant.MemberId)).ProtectionId);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var member = setup.Members[descendantIndex];
        var proof = await second.ReadNamespaceMemberProtectionEpochAsync(member.MemberId);
        Assert.AreEqual(originalEpoch.ProtectionId, proof.ProtectionId);
        var lineage = await second.ReadNamespaceMemberProtectionLineageAsync(member.MemberId);
        Assert.AreEqual(rootEpoch.ProtectionId, lineage.Single(item => item.Member.MemberId == setup.Move.Member.MemberId).Protection.ProtectionId);
        Assert.AreEqual(setup.Move.Move.OperationId, lineage[0].Location.CurrentPositionOperationId);
        Assert.AreEqual(originalEpoch, await second.ReadLatestNamespaceBirthProtectionAsync(member.OriginId));
        Assert.AreEqual(member, await second.ReadNamespaceMemberAsync(member.MemberId));
        Assert.AreEqual(firstBirth, await second.ReadNamespaceBirthAsync(member.OriginId));
        Assert.AreEqual(firstObservation, await second.ReadNamespaceBirthObservationAsync(member.OriginId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RegisteredDescendantStillRejectsAChangedCurrentAncestorRole()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        var time = setup.Completion.Root.VerifiedAt.AddSeconds(1);
        await RotateContextOriginalParentsAsync(catalog, setup.Move, time);
        var rootEpoch = await LocatedBornEpochAsync(catalog, setup.Move.Member, time.AddSeconds(4));
        await catalog.RecordNamespaceBirthProtectionAsync(rootEpoch);
        foreach (var descendant in setup.Members.Skip(1))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionEpochAsync(descendant.MemberId));
        Assert.AreEqual(setup.Move.Born.Birth, await catalog.ReadNamespaceBirthAsync(setup.Move.Born.Birth.OperationId));
    }

    [TestMethod]
    public async Task NewBirthCannotUseHistoricalParentEpochAfterPlacementRefresh()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        var time = setup.Completion.Root.VerifiedAt.AddSeconds(1);
        var rootEpoch = await LocatedBornEpochAsync(catalog, setup.Move.Member, time);
        await catalog.RecordNamespaceBirthProtectionAsync(rootEpoch);
        var stale = NestedBirth(setup.Move.Born, "stale-child.txt", false) with { PreparedAt = time.AddSeconds(1) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(stale));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(stale.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(stale.OperationId));
    }
}
