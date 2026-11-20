using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FencedRecoveryReadsMovedMembersAcrossMixedParentRolesWithoutWeakeningOrdinaryAuthorization(bool original)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveCompletion moved;
        Guid initialization = Guid.NewGuid();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (original) setup = await WithOriginalMovableFileAsync(catalog, setup);
            moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
            await ProtectAsync(catalog, setup.Parent, RotateOriginal(setup.ParentProof, RelocatedRole, moved.VerifiedAt.AddSeconds(1)));
            await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.RootId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        var lineage = await reopened.ReadNamespaceMemberRecoveryLineageAsync(initialization, setup.Member.MemberId);
        Assert.HasCount(3, lineage);
        Assert.AreEqual(setup.Member, lineage[0].Member); Assert.AreEqual(moved.RelativePath, lineage[0].Location.RelativePath);
        Assert.AreEqual(moved.OperationId, lineage[0].Location.CurrentPositionOperationId);
        Assert.AreEqual(RelocatedRole, lineage[1].Permission!.Intent.RoleSid);
        Assert.AreNotEqual(RelocatedRole, original ? lineage[0].Permission!.Intent.RoleSid : lineage[0].BirthProtection!.RoleSid);
        if (original)
        {
            Assert.AreEqual(setup.Member.OriginId, lineage[0].Original!.EvidenceId);
            Assert.AreEqual(setup.Member.RelativePath, lineage[0].Original!.RelativePath); Assert.IsNull(lineage[0].BirthObservation);
        }
        else
        {
            Assert.AreEqual(setup.Born.Protection, lineage[0].BirthProtection);
            Assert.AreEqual(setup.Born.Observation, lineage[0].BirthObservation); Assert.IsNull(lineage[0].Original);
        }
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        CollectionAssert.AreEqual(new[] { setup.Move.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RecoveryRetainsAnUnfinishedOriginalPermissionIntentWithoutInventingItsVerification()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); Guid initialization = Guid.NewGuid();
        var intent = RotateOriginal(setup.ParentProof, RelocatedRole, setup.Member.RegisteredAt.AddSeconds(1));
        await catalog.PrepareNamespacePermissionChangeAsync(setup.Parent, intent);
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.RootId));
        var recovery = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, setup.Member.MemberId);
        Assert.AreEqual(intent, recovery[1].Permission!.Intent);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, recovery[1].Permission!.Phase);
        Assert.IsNull(recovery[1].Permission!.Verification); Assert.IsNull(recovery[1].Permission!.AppliedAt);
        Assert.AreEqual(setup.Born.Protection, recovery[0].BirthProtection);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task RecoveryUsesSettledAncestorPositionsAndBornParentsWithoutRecapturingOriginalMembership()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        await RotateContextOriginalParentsAsync(catalog, setup.Move, setup.Completion.Root.VerifiedAt.AddSeconds(1));
        Guid initialization = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.Move.RootId));
        var member = setup.Members[^1];
        var recovery = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, member.MemberId);
        Assert.HasCount(5, recovery); Assert.AreEqual(member, recovery[0].Member);
        Assert.AreEqual(setup.Completion.OperationId, recovery[0].Location.CurrentPositionOperationId);
        Assert.AreEqual(setup.Completion.Entries.Single(entry => entry.MemberId == member.MemberId).Completion.RelativePath,
            recovery[0].Location.RelativePath);
        Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, recovery[1].Member.Origin);
        Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, recovery[2].Member.Origin);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId));
    }

    [TestMethod]
    public async Task RecoveryRejectsForeignOwnersAndLostReservations()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); Guid foreign = Guid.NewGuid(), owned = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(foreign, RootId.New()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(foreign, setup.Member.MemberId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(Guid.NewGuid(), setup.Member.MemberId));
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(owned, setup.Move.RootId));
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_initialization_roots WHERE operation_id='{owned:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(owned, setup.Member.MemberId));
    }

    [TestMethod]
    public async Task RecoveryCannotAdoptRetiredMembersOrParentsRestoredToOrdinaryPermissions()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var retirement = Retire(setup, Guid.NewGuid());
        await catalog.PrepareNamespaceRetirementAsync(retirement);
        await catalog.RecordNamespaceRetirementStartAsync(retirement.OperationId, retirement.PreparedAt);
        await catalog.RecordNamespaceRetirementNativeDeletedAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(1));
        await catalog.CompleteNamespaceRetirementAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(2));
        Guid initialization = Guid.NewGuid(); await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.RootId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, setup.Member.MemberId));
        var restore = setup.ParentProof with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = setup.ParentProof.TargetDacl,
            TargetDacl = setup.Parent.OriginalDacl,
            PreparedAt = retirement.PreparedAt.AddSeconds(3)
        };
        await ProtectAsync(catalog, setup.Parent, restore);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization, setup.ParentMember.MemberId));
    }

    private static MirrorPulseNamespaceInitializationIntent RecoveryAdmission(Guid operation, RootId root) =>
        new(1, operation, [new(root, Guid.NewGuid())], DateTimeOffset.UtcNow);
}
