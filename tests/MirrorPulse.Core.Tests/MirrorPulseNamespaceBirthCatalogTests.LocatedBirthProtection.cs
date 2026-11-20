using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task LocatedBornProtectionRotatesAtTheMovedObjectAndRetainsOriginAcrossOwners()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceBirthProtection epoch;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
            await RotateContextOriginalParentsAsync(catalog, setup, moved.VerifiedAt.AddSeconds(1));
            epoch = await LocatedBornEpochAsync(catalog, setup.Member, moved.VerifiedAt.AddSeconds(5));
            await catalog.RecordNamespaceBirthProtectionAsync(epoch);
            var context = (await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Member.MemberId))[0];
            Assert.AreEqual(epoch.ProtectionId, context.Protection.ProtectionId);
            Assert.AreEqual(RelocatedRole, context.Protection.RoleSid);
            Assert.AreEqual(moved.RelativePath, context.Location.RelativePath);
            Assert.AreEqual(epoch.Verification, context.Verification);
            Assert.AreEqual(moved.Child, context.PositionVerification);
        }
        await using var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(epoch, await owner.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        Assert.AreEqual(epoch.ProtectionId, (await owner.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId)).ProtectionId);
        Assert.AreEqual(setup.Born.Birth, await owner.ReadNamespaceBirthAsync(setup.Member.OriginId));
        Assert.AreEqual(setup.Born.Observation, await owner.ReadNamespaceBirthObservationAsync(setup.Member.OriginId));
        Assert.AreEqual(setup.Member, await owner.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(epoch, await owner.RecordNamespaceBirthProtectionAsync(epoch));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task LocatedBornParentAndChildRolesSupportALaterMoveWithoutChangingFirstParents()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog, movedChild: true);
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        var time = setup.Completion.Root.VerifiedAt.AddSeconds(1);
        await RotateContextOriginalParentsAsync(catalog, setup.Move, time);
        var rootEpoch = await LocatedBornEpochAsync(catalog, setup.Move.Member, time.AddSeconds(4));
        await catalog.RecordNamespaceBirthProtectionAsync(rootEpoch);
        var nested = setup.Members.Single(member => member.MemberId != setup.Move.Member.MemberId && member.IsDirectory);
        var nestedEpoch = await LocatedBornEpochAsync(catalog, nested, time.AddSeconds(5));
        await catalog.RecordNamespaceBirthProtectionAsync(nestedEpoch);
        var grandEpoch = await LocatedBornEpochAsync(catalog, setup.Members[^1], time.AddSeconds(6));
        await catalog.RecordNamespaceBirthProtectionAsync(grandEpoch);
        Assert.HasCount(5, await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Members[^1].MemberId));
        var childEpoch = await LocatedBornEpochAsync(catalog, setup.Child, time.AddSeconds(7));
        await catalog.RecordNamespaceBirthProtectionAsync(childEpoch);
        var child = (await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Child.MemberId))[0];
        var identity = (await catalog.ReadNamespaceMemberIdentityAsync(setup.Child.MemberId))!;
        var parentEpoch = (await catalog.ReadNamespaceMemberProtectionEpochAsync(setup.Move.ParentMember.MemberId)).ProtectionId;
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), setup.Child.MemberId,
            childEpoch.ProtectionId, setup.Move.Move.RootId, setup.Child.LocalObject, identity.ItemId!.Value, identity.RemoteId!,
            setup.Child.OwnerSid, RelocatedRole,
            new(setup.Move.Member.MemberId, setup.Move.Member.LocalObject, rootEpoch.ProtectionId, child.Location.RelativePath)
            { ParentPositionOperationId = setup.Completion.OperationId },
            new(setup.Move.ParentMember.MemberId, setup.Move.ParentMember.LocalObject, parentEpoch, "Docs/after-rotation.txt"), time.AddSeconds(8))
        { PreviousMoveOperationId = setup.ChildMoveId, SourcePositionOperationId = setup.Completion.OperationId };
        var completed = await CompleteMoveChainStepAsync(catalog, move);
        var later = await catalog.ReadNamespaceMemberProtectionLineageAsync(setup.Child.MemberId);
        Assert.AreEqual(childEpoch.ProtectionId, later[0].Protection.ProtectionId);
        Assert.AreEqual(setup.Move.ParentMember, later[1].Member);
        Assert.AreEqual(completed.RelativePath, later[0].Location.RelativePath);
        Assert.AreEqual(setup.Move.Member.MemberId, later[0].Member.ParentMemberId);
        var refreshed = await LocatedBornEpochAsync(catalog, setup.Child, completed.VerifiedAt.AddSeconds(1));
        await catalog.RecordNamespaceBirthProtectionAsync(refreshed);
        Assert.AreEqual(setup.Move.ParentMember.MemberId, refreshed.Placement!.ParentMemberId);
        Assert.AreEqual(childEpoch.BornParent, refreshed.BornParent);
        Assert.AreEqual(setup.Child, await catalog.ReadNamespaceMemberAsync(setup.Child.MemberId));
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("member")]
    [DataRow("parent")]
    [DataRow("first-parent")]
    [DataRow("position-time")]
    [DataRow("parent-time")]
    [DataRow("stale-parent")]
    public async Task LocatedBornProtectionRejectsUnprovedPlacementParentOrChronology(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var parentTime = moved.VerifiedAt.AddSeconds(1);
        await RotateContextOriginalParentsAsync(catalog, setup, parentTime);
        var epoch = await LocatedBornEpochAsync(catalog, setup.Member, parentTime.AddSeconds(4));
        var invalid = mismatch switch
        {
            "path" => epoch with { Placement = epoch.Placement! with { RelativePath = setup.Member.RelativePath } },
            "member" => epoch with { Placement = epoch.Placement! with { MemberId = setup.ParentMember.ParentMemberId!.Value } },
            "parent" => epoch with { Placement = epoch.Placement! with { ParentMemberId = setup.ParentMember.ParentMemberId!.Value } },
            "first-parent" => epoch with { ParentPermissionOperationId = epoch.Placement!.ParentProtectionId },
            "position-time" => epoch with { Verification = epoch.Verification with { ObservedAt = moved.VerifiedAt } },
            "parent-time" => epoch with { Verification = epoch.Verification with { ObservedAt = parentTime.AddSeconds(2) } },
            _ => epoch with
            {
                RoleSid = setup.Born.Protection.RoleSid,
                Verification = epoch.Verification with { Dacl = setup.Born.Protection.Verification.Dacl },
                Placement = epoch.Placement! with { ParentProtectionId = setup.ParentProof.OperationId }
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(invalid));
        Assert.AreEqual(setup.Born.Protection, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
        await catalog.RecordNamespaceBirthProtectionAsync(epoch);
        Assert.AreEqual(epoch, await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Member.OriginId));
    }

    [TestMethod]
    public async Task LocatedBornProtectionDoesNotAddPlacementToLegacyPayloads()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsFalse(JsonSerializer.Serialize(setup.Born.Protection).Contains("Placement", StringComparison.Ordinal));
        var parentTime = setup.Member.RegisteredAt.AddSeconds(1);
        await RotateContextOriginalParentsAsync(catalog, setup, parentTime);
        var epoch = await LocatedBornEpochAsync(catalog, setup.Member, parentTime.AddSeconds(4));
        Assert.IsNull(epoch.Placement!.PositionOperationId);
        await catalog.RecordNamespaceBirthProtectionAsync(epoch);
        Assert.AreEqual(epoch.ProtectionId, (await catalog.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId)).ProtectionId);
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("parent-position")]
    public async Task LocatedBornHistoryRejectsRecomputedPlacementCorruptionAfterPriorReadAndReopen(string mismatch)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceBirthProtection epoch;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
            await RotateContextOriginalParentsAsync(catalog, setup, moved.VerifiedAt.AddSeconds(1));
            epoch = await LocatedBornEpochAsync(catalog, setup.Member, moved.VerifiedAt.AddSeconds(5));
            await catalog.RecordNamespaceBirthProtectionAsync(epoch);
            Assert.AreEqual(epoch, await catalog.ReadLatestNamespaceBirthProtectionAsync(epoch.BirthOperationId));
            var corrupt = epoch with
            {
                Placement = mismatch == "path" ? epoch.Placement! with { RelativePath = "Docs/unobserved.txt" }
                    : epoch.Placement! with { ParentPositionOperationId = epoch.Placement.PositionOperationId }
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(corrupt);
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_birth_protections SET payload=$payload,fingerprint=$hash WHERE protection_id=$id;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$id", epoch.ProtectionId.ToString("D"));
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadLatestNamespaceBirthProtectionAsync(epoch.BirthOperationId));
        }
        await using var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.ReadLatestNamespaceBirthProtectionAsync(epoch.BirthOperationId));
    }

    [TestMethod]
    public async Task BirthAdmissionCanRetainTheLocatedParentRoleWithoutRewritingItsFirstBirth()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var directory = await PrepareDirectoryCompletionFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(directory);
        await RotateContextOriginalParentsAsync(catalog, setup, directory.Root.VerifiedAt.AddSeconds(1));
        var epoch = await LocatedBornEpochAsync(catalog, setup.Member, directory.Root.VerifiedAt.AddSeconds(5));
        await catalog.RecordNamespaceBirthProtectionAsync(epoch);
        var child = NestedBirth(setup.Born, "new-child.txt", false) with
        {
            RelativePath = epoch.Placement!.RelativePath + "/new-child.txt",
            RoleSid = epoch.RoleSid,
            BornParent = new(setup.Born.Birth.OperationId, epoch.ProtectionId),
            PreparedAt = epoch.Verification.ObservedAt.AddSeconds(1)
        };
        Assert.AreEqual(child, await catalog.PrepareNamespaceBirthAsync(child));
        Assert.AreEqual(child, await catalog.ReadNamespaceBirthAsync(child.OperationId));
        Assert.AreEqual(setup.Born.Birth, await catalog.ReadNamespaceBirthAsync(setup.Born.Birth.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        // This tests retained admission only. Registered-child placement and native birth
        // execution require their own explicit position and fresh-object boundaries.
    }

    [TestMethod]
    public async Task LocatedBornEpochCannotAuthorizeTheEarlierMoveThatItsPlacementDependsOn()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
        await RotateContextOriginalParentsAsync(catalog, setup, moved.VerifiedAt.AddSeconds(1));
        var epoch = await LocatedBornEpochAsync(catalog, setup.Member, moved.VerifiedAt.AddSeconds(5));
        await catalog.RecordNamespaceBirthProtectionAsync(epoch);
        var corrupt = setup.Move with { ProtectionId = epoch.ProtectionId };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(corrupt);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE namespace_member_moves SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        update.Parameters.AddWithValue("$id", moved.OperationId.ToString("D"));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberMoveAsync(moved.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadLatestNamespaceBirthProtectionAsync(epoch.BirthOperationId));
    }

    private static async Task RotateContextOriginalParentsAsync(MirrorPulseProductCatalog catalog, MoveFixture setup, DateTimeOffset time)
    {
        await ProtectAsync(catalog, setup.Parent, RotateOriginal(setup.ParentProof, RelocatedRole, time));
        var anchor = (await catalog.ReadNamespaceMemberAsync(setup.ParentMember.ParentMemberId!.Value))!;
        var original = (await catalog.ReadNamespacePermissionBaselineAsync(anchor.OriginId))!;
        var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(anchor.OriginId))!.Intent;
        await ProtectAsync(catalog, original, RotateOriginal(previous, RelocatedRole, time));
    }

    private static async Task<MirrorPulseNamespaceBirthProtection> LocatedBornEpochAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, DateTimeOffset time)
    {
        var birth = (await catalog.ReadNamespaceBirthAsync(member.OriginId))!;
        var previous = (await catalog.ReadLatestNamespaceBirthProtectionAsync(member.OriginId))!;
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId);
        var parent = (await catalog.ReadNamespaceMemberProtectionLineageAsync(location.ParentMemberId!.Value))[0];
        return previous with
        {
            Version = 3,
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = previous.ProtectionId,
            ParentPermissionOperationId = birth.ParentPermissionOperationId,
            BornParent = birth.BornParent,
            RoleSid = parent.Protection.RoleSid,
            Verification = previous.Verification with
            { Dacl = previous.Verification.Dacl.Replace(previous.RoleSid, parent.Protection.RoleSid, StringComparison.Ordinal), ObservedAt = time },
            Placement = new(member.MemberId, location.CurrentPositionOperationId, parent.Member.MemberId,
                parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId, location.RelativePath)
        };
    }
}
