using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task FirstParentPlacementRegistersNewDescendantsAfterDirectoryMoveAcrossOwners()
    {
        using var fixture = new Fixture();
        PlacedChildFixture setup;
        MirrorPulseNamespaceMemberRegistration grandchild;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareFirstPlacedChildAsync(catalog, directory: true);
            Assert.AreEqual(setup.Member, await catalog.RegisterNamespaceMemberAsync(setup.Member));
            var grand = await AddBornAsync(catalog, NestedBirth(setup.Born, "grand.txt", false));
            grandchild = BornMember(grand, setup.Member);
            await catalog.RegisterNamespaceMemberAsync(grandchild);
            Assert.AreEqual(setup.Parent.Move.Member, await catalog.ReadNamespaceMemberAsync(setup.Parent.Move.Member.MemberId));
            Assert.AreEqual(setup.Parent.Move.Born.Birth, await catalog.ReadNamespaceBirthAsync(setup.Parent.Move.Member.OriginId));
            Assert.HasCount(5, await catalog.ReadNamespaceMemberProtectionLineageAsync(grandchild.MemberId));
        }
        await using var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Member, await owner.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(grandchild, await owner.ReadNamespaceMemberAsync(grandchild.MemberId));
        Assert.AreEqual(setup.Member, await owner.RegisterNamespaceMemberAsync(setup.Member));
        Assert.AreEqual(grandchild, await owner.RegisterNamespaceMemberAsync(grandchild));
        Assert.AreEqual(setup.Member, await owner.ReadNamespaceMemberChildAsync(setup.Parent.Move.Member.MemberId, "after.txt"));
        Assert.AreEqual(setup.Member.RelativePath, (await owner.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
        Assert.AreEqual(setup.Born.Birth, await owner.ReadNamespaceBirthAsync(setup.Member.OriginId));
        Assert.IsFalse(await FirstPlacedChildOwnsReservationAsync(owner, setup.Member.OriginId));
        Assert.IsFalse(JsonSerializer.Serialize(grandchild).Contains("FirstParentPositionOperationId", StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("legacy-with-position")]
    [DataRow("missing-position")]
    [DataRow("empty-position")]
    [DataRow("original-origin")]
    [DataRow("unproved-position")]
    [DataRow("legacy-without-position")]
    [DataRow("first-path")]
    [DataRow("foreign-parent")]
    public async Task FirstParentPlacementRejectsUnprovedOrSubstitutedInitialParent(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareFirstPlacedChildAsync(catalog);
        var invalid = mismatch switch
        {
            "legacy-with-position" => setup.Member with { Version = 1 },
            "missing-position" => setup.Member with { FirstParentPositionOperationId = null },
            "empty-position" => setup.Member with { FirstParentPositionOperationId = Guid.Empty },
            "original-origin" => setup.Member with { Origin = MirrorPulseNamespaceMemberOrigin.CapturedOriginal },
            "unproved-position" => setup.Member with { FirstParentPositionOperationId = Guid.NewGuid() },
            "legacy-without-position" => setup.Member with { Version = 1, FirstParentPositionOperationId = null },
            "first-path" => setup.Member with { RelativePath = setup.Parent.Move.Member.RelativePath + "/after.txt" },
            "foreign-parent" => setup.Member with { ParentMemberId = setup.Parent.Move.ParentMember.MemberId },
            _ => throw new ArgumentException("Unknown mismatch.", nameof(mismatch))
        };
        if (mismatch is "legacy-with-position" or "missing-position" or "empty-position" or "original-origin")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RegisterNamespaceMemberAsync(invalid));
        else if (mismatch == "unproved-position")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RegisterNamespaceMemberAsync(invalid));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.IsTrue(await FirstPlacedChildOwnsReservationAsync(catalog, setup.Member.OriginId));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.Parent.Move.Member.MemberId, "after.txt"));
        await catalog.RegisterNamespaceMemberAsync(setup.Member);
    }

    [TestMethod]
    public async Task FirstParentPlacementRemainsHistoricalAfterALaterWholeDirectoryMove()
    {
        using var fixture = new Fixture();
        PlacedChildFixture setup;
        MirrorPulseNamespaceDirectoryMoveCompletion second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareFirstPlacedChildAsync(catalog);
            await catalog.RegisterNamespaceMemberAsync(setup.Member);
            var time = setup.Member.RegisteredAt.AddSeconds(1);
            int index = 0;
            foreach (var member in setup.Parent.Members.Skip(1))
                await catalog.RecordNamespaceBirthProtectionAsync(await LocatedBornEpochAsync(catalog, member, time.AddSeconds(index++)));
            var rootEpoch = (await catalog.ReadLatestNamespaceBirthProtectionAsync(setup.Parent.Move.Member.OriginId))!;
            var parentProof = (await catalog.ReadLatestNamespacePermissionChangeAsync(setup.Parent.Move.ParentMember.OriginId))!.Intent;
            var next = setup.Parent.Move with
            {
                ParentProof = parentProof,
                Born = setup.Parent.Move.Born with { Protection = rootEpoch },
                Move = setup.Parent.Move.Move with
                {
                    OperationId = Guid.NewGuid(),
                    ProtectionId = rootEpoch.ProtectionId,
                    RoleSid = rootEpoch.RoleSid,
                    PreviousMoveOperationId = setup.Parent.Completion.OperationId,
                    SourcePositionOperationId = setup.Parent.Completion.OperationId,
                    Source = setup.Parent.Move.Move.Target with { ParentProtectionId = parentProof.OperationId },
                    Target = setup.Parent.Move.Move.Target with { ParentProtectionId = parentProof.OperationId, RelativePath = "Docs/later-directory" },
                    PreparedAt = time.AddSeconds(10),
                    DirectoryMoveEvidence = "AQ=="
                }
            };
            second = await PrepareDirectoryCompletionFactsAsync(catalog, next);
            // The shared legacy fixture uses the first role for descendants. These
            // fresh synthetic observations must instead reflect the retained rotated role.
            second = second with
            {
                Entries = second.Entries.Select(entry => entry with
                {
                    Native = entry.Native with
                    {
                        Child = entry.Native.Child with { Dacl = entry.Native.Child.Dacl.Replace(Role, rootEpoch.RoleSid, StringComparison.Ordinal) },
                        Parent = entry.Native.Parent with { Dacl = entry.Native.Parent.Dacl.Replace(Role, rootEpoch.RoleSid, StringComparison.Ordinal) }
                    },
                    Completion = entry.Completion with
                    {
                        Child = entry.Completion.Child with { Dacl = entry.Completion.Child.Dacl.Replace(Role, rootEpoch.RoleSid, StringComparison.Ordinal) },
                        Parent = entry.Completion.Parent with { Dacl = entry.Completion.Parent.Dacl.Replace(Role, rootEpoch.RoleSid, StringComparison.Ordinal) }
                    }
                }).ToArray()
            };
            await catalog.CompleteNamespaceDirectoryMoveAsync(second);
            var current = await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId);
            Assert.AreEqual("Docs/later-directory/after.txt", current.RelativePath);
            Assert.AreEqual(second.OperationId, current.CurrentPositionOperationId);
            Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
            Assert.AreEqual(setup.Parent.Completion.OperationId, setup.Member.FirstParentPositionOperationId);
            Assert.AreEqual(setup.Member, await catalog.RegisterNamespaceMemberAsync(setup.Member));
        }
        await using var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Member, await owner.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(second.OperationId, (await owner.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        Assert.AreEqual(setup.Born.Birth, await owner.ReadNamespaceBirthAsync(setup.Member.OriginId));
    }

    [TestMethod]
    public async Task FirstParentPlacementRejectsChangedReferenceWithRecomputedCanonicalHashAcrossOwners()
    {
        using var fixture = new Fixture();
        PlacedChildFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareFirstPlacedChildAsync(catalog);
            await catalog.RegisterNamespaceMemberAsync(setup.Member);
            var changed = setup.Member with { FirstParentPositionOperationId = Guid.NewGuid() };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(changed));
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed);
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_members SET payload=$payload,fingerprint=$hash WHERE member_id=$id;";
            update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$id", setup.Member.MemberId.ToString("D"));
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        }
        await using var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Parent.Move.Member, await owner.ReadNamespaceMemberAsync(setup.Parent.Move.Member.MemberId));
    }

    private sealed record PlacedChildFixture(DirectorySettlementFixture Parent, BornObject Born,
        MirrorPulseNamespaceMemberRegistration Member);

    private static async Task<bool> FirstPlacedChildOwnsReservationAsync(MirrorPulseProductCatalog catalog, Guid birthId)
    {
        var scan = await catalog.BeginNamespaceBirthRecoveryScanAsync();
        var page = await catalog.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 32);
        Assert.IsFalse(page.HasMore);
        return page.Entries.Single(entry => entry.Intent.OperationId == birthId).OwnsNameReservation;
    }

    private static async Task<PlacedChildFixture> PrepareFirstPlacedChildAsync(MirrorPulseProductCatalog catalog, bool directory = false)
    {
        var parent = await PrepareDirectorySettlementAsync(catalog);
        await catalog.CompleteNamespaceDirectoryMoveAsync(parent.Completion);
        var time = parent.Completion.Root.VerifiedAt.AddSeconds(1);
        await RotateContextOriginalParentsAsync(catalog, parent.Move, time);
        var epoch = await LocatedBornEpochAsync(catalog, parent.Move.Member, time.AddSeconds(4));
        await catalog.RecordNamespaceBirthProtectionAsync(epoch);
        var birth = NestedBirth(parent.Move.Born, "after.txt", directory) with
        {
            RelativePath = epoch.Placement!.RelativePath + "/after.txt",
            RoleSid = epoch.RoleSid,
            BornParent = new(parent.Move.Member.OriginId, epoch.ProtectionId),
            PreparedAt = epoch.Verification.ObservedAt.AddSeconds(1)
        };
        var born = await AddBornAsync(catalog, birth);
        var member = BornMember(born, parent.Move.Member) with
        { Version = 2, FirstParentPositionOperationId = parent.Completion.OperationId };
        return new(parent, born, member);
    }
}
