using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectionContextRetainsFirstFactsAndCompletedFileLocationAcrossOwners(bool original)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveCompletion completion;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            if (original) setup = await WithOriginalMovableFileAsync(first, setup);
            var before = await first.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId);
            Assert.AreEqual(setup.Member.RelativePath, before[0].Location.RelativePath);
            Assert.IsNull(before[0].PositionVerification);
            completion = await CompleteMoveChainStepAsync(first, setup.Move);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var lineage = await second.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId);
        Assert.HasCount(3, lineage);
        Assert.AreEqual(setup.Member, lineage[0].Member);
        Assert.AreEqual(completion.RelativePath, lineage[0].Location.RelativePath);
        Assert.AreEqual(completion.OperationId, lineage[0].Location.CurrentPositionOperationId);
        Assert.AreEqual(setup.Move.ProtectionId, lineage[0].Protection.ProtectionId);
        Assert.AreEqual(completion.Child, lineage[0].PositionVerification);
        Assert.AreEqual(setup.ParentMember, lineage[1].Member);
        Assert.AreEqual(string.Empty, lineage[^1].Location.RelativePath);
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task ProtectionContextUsesAncestorSettlementAndActualParentAfterLaterChildMove()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Child.MemberId));
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        foreach (var member in setup.Members)
        {
            var lineage = await catalog.ReadNamespaceMemberProtectionLineageAsync(member.MemberId);
            var retained = setup.Completion.Entries.Single(entry => entry.MemberId == member.MemberId);
            Assert.AreEqual(retained.Completion.RelativePath, lineage[0].Location.RelativePath);
            Assert.AreEqual(retained.Completion.Child, lineage[0].PositionVerification);
            Assert.AreEqual(setup.Completion.OperationId, lineage[0].Location.CurrentPositionOperationId);
        }
        var context = (await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Child.MemberId))[0];
        var identity = (await catalog.ReadNamespaceMemberIdentityAsync(setup.Child.MemberId))!;
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), setup.Child.MemberId,
            context.Protection.ProtectionId, setup.Move.Move.RootId, setup.Child.LocalObject, identity.ItemId!.Value,
            identity.RemoteId!, setup.Child.OwnerSid, context.Protection.RoleSid,
            new(setup.Move.Member.MemberId, setup.Move.Member.LocalObject, setup.Move.Born.Protection.ProtectionId, context.Location.RelativePath)
            { ParentPositionOperationId = setup.Completion.OperationId },
            new(setup.Move.ParentMember.MemberId, setup.Move.ParentMember.LocalObject, setup.Move.ParentProof.OperationId, "Docs/outside.txt"),
            setup.Completion.Root.VerifiedAt.AddSeconds(1))
        { PreviousMoveOperationId = setup.ChildMoveId, SourcePositionOperationId = setup.Completion.OperationId };
        var completed = await CompleteMoveChainStepAsync(catalog, move);
        var actual = await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Child.MemberId);
        Assert.HasCount(3, actual);
        Assert.AreEqual(setup.Move.ParentMember, actual[1].Member);
        Assert.AreEqual(completed.RelativePath, actual[0].Location.RelativePath);
        Assert.AreEqual(setup.Move.Member.MemberId, actual[0].Member.ParentMemberId);
        Assert.AreEqual(setup.Child, await catalog.ReadNamespaceMemberAsync(setup.Child.MemberId));
        Assert.AreEqual(setup.Move.Born.Birth, await catalog.ReadNamespaceBirthAsync(setup.Move.Born.Birth.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectionContextRejectsAChangedCurrentParentWhileHistoricalBirthRemainsReadable(bool restore)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var previous = setup.ParentProof;
        var change = restore ? previous with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = previous.TargetDacl,
            TargetDacl = setup.Parent.OriginalDacl,
            PreparedAt = setup.Member.RegisteredAt.AddSeconds(1)
        } : RotateOriginal(previous, RelocatedRole, setup.Member.RegisteredAt.AddSeconds(1));
        await ProtectAsync(catalog, setup.Parent, change);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Born.Protection, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task ProtectionContextRejectsAnUnlocatedLaterBornEpochAfterMovement()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var unlocated = setup.Born.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = setup.Born.Protection.ProtectionId,
            Verification = setup.Born.Protection.Verification with { ObservedAt = moved.VerifiedAt.AddSeconds(1) }
        };
        await catalog.RecordNamespaceBirthProtectionAsync(unlocated);
        Assert.AreEqual(unlocated, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task ProtectionContextAcceptsAnExplicitOriginalEpochAtItsCurrentLocation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await WithOriginalMovableFileAsync(catalog, await PrepareMovableMemberAsync(catalog));
        var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var original = (await catalog.ReadNamespacePermissionBaselineAsync(setup.Member.OriginId))!;
        var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(original.EvidenceId))!.Intent;
        var located = RotateOriginal(previous, RelocatedRole, moved.VerifiedAt.AddSeconds(1)) with
        {
            RelativePath = moved.RelativePath,
            Relocation = new(setup.Member.MemberId, moved.OperationId),
        };
        await ProtectAsync(catalog, original, located);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId));
        await ProtectAsync(catalog, setup.Parent, RotateOriginal(setup.ParentProof, RelocatedRole, moved.VerifiedAt.AddSeconds(1)));
        var anchor = (await catalog.ReadNamespaceMemberAsync(setup.ParentMember.ParentMemberId!.Value))!;
        var anchorOriginal = (await catalog.ReadNamespacePermissionBaselineAsync(anchor.OriginId))!;
        var anchorPrevious = (await catalog.ReadLatestNamespacePermissionChangeAsync(anchor.OriginId))!.Intent;
        await ProtectAsync(catalog, anchorOriginal, RotateOriginal(anchorPrevious, RelocatedRole, moved.VerifiedAt.AddSeconds(1)));
        var current = (await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId))[0];
        Assert.AreEqual(located.OperationId, current.Protection.ProtectionId);
        Assert.AreEqual(moved.RelativePath, current.Location.RelativePath);
        Assert.AreEqual(moved.Child, current.PositionVerification);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }
}
