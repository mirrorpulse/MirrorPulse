using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task EntireDirectorySettlementRetainsEveryPositionAndOriginalAcrossOwners()
    {
        using var fixture = new Fixture();
        DirectorySettlementFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareDirectorySettlementAsync(first, movedChild: true);
            await first.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
            Assert.IsNull(await first.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(setup.Move.ParentMember.MemberId, "new.txt"));
            Assert.AreEqual(setup.Move.Member, await first.ReadNamespaceMemberChildAsync(setup.Move.ParentMember.MemberId, "moved.txt"));
            foreach (var member in setup.Members)
            {
                Assert.AreEqual(member, await first.ReadNamespaceMemberAsync(member.MemberId));
                var position = await first.ReadNamespaceMemberPositionAsync(member.MemberId, setup.Completion.OperationId);
                Assert.IsNotNull(position);
                Assert.AreEqual(setup.Completion.OperationId, position.OperationId);
                Assert.AreEqual(member.MemberId == setup.Move.Member.MemberId ? setup.Completion.OperationId :
                    member.MemberId == setup.Child.MemberId ? setup.ChildMoveId : null, position.CompletedMoveOperationId);
            }
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await second.ReadNamespaceDirectoryMoveCompletionAsync(setup.Completion.OperationId);
        Assert.IsNotNull(retained);
        CollectionAssert.AreEqual(setup.Completion.Entries.ToArray(), retained.Entries.ToArray());
        Assert.AreEqual(setup.Completion.Root, await second.ReadNamespaceMemberMoveCompletionAsync(setup.Completion.OperationId));
        var child = await second.ReadNamespaceMemberLocationAsync(setup.Child.MemberId);
        Assert.AreEqual("Docs/moved.txt/renamed.txt", child.RelativePath);
        Assert.AreEqual(setup.ChildMoveId, child.CompletedMoveOperationId);
        Assert.AreEqual(setup.Completion.OperationId, child.PositionOperationId);
        var tree = await second.ReadNamespaceMemberTreeAsync(setup.Move.Member.MemberId);
        Assert.HasCount(4, tree);
        Assert.IsTrue(tree.All(entry => entry.RelativePath.StartsWith("Docs/moved.txt", StringComparison.Ordinal)));
        await second.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task DescendantPositionFailureRollsBackEntireDirectoryNameReceiptAndFenceHandover()
    {
        using var fixture = new Fixture();
        DirectorySettlementFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareDirectorySettlementAsync(first);
            await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_subtree_position BEFORE INSERT ON namespace_member_positions WHEN NEW.member_id='{setup.Members[^1].MemberId:D}' BEGIN SELECT RAISE(ABORT,'last descendant interruption'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => first.CompleteNamespaceDirectoryMoveAsync(setup.Completion));
            Assert.IsNull(await first.ReadNamespaceDirectoryMoveCompletionAsync(setup.Completion.OperationId));
            Assert.IsNull(await first.ReadNamespaceMemberMoveCompletionAsync(setup.Completion.OperationId));
            Assert.IsNotNull(await first.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
            foreach (var member in setup.Members)
            {
                Assert.IsNull(await first.ReadNamespaceMemberPositionAsync(member.MemberId, setup.Completion.OperationId));
                Assert.AreEqual(member.RelativePath, (await first.ReadNamespaceMemberLocationAsync(member.MemberId)).RelativePath);
            }
            Assert.AreEqual(setup.Move.Member, await first.ReadNamespaceMemberChildAsync(setup.Move.ParentMember.MemberId, "new.txt"));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_subtree_position;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await second.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
        Assert.IsNotNull(await second.ReadNamespaceDirectoryMoveCompletionAsync(setup.Completion.OperationId));
        Assert.IsNull(await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("parent")]
    [DataRow("item")]
    [DataRow("native-dacl")]
    [DataRow("path")]
    [DataRow("projection-item")]
    [DataRow("fresh-time")]
    [DataRow("proof")]
    public async Task DirectorySettlementRejectsAChangedDescendantOrPublicEvidence(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        var original = setup.Completion.Entries[1];
        var altered = mismatch switch
        {
            "binding" => original with { Native = original.Native with { Child = original.Native.Child with { LocalObject = original.Native.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } } },
            "parent" => original with { ParentMemberId = setup.Move.ParentMember.MemberId },
            "item" => original with { Native = original.Native with { ItemId = Guid.NewGuid() } },
            "native-dacl" => original with { Native = original.Native with { Child = original.Native.Child with { Dacl = setup.Move.Parent.OriginalDacl } } },
            "path" => original with { Native = original.Native with { RelativePath = setup.Child.RelativePath } },
            "projection-item" => original with { Projection = original.Projection with { ItemId = Guid.NewGuid() } },
            "fresh-time" => original with { Completion = original.Completion with { Child = original.Completion.Child with { ObservedAt = original.Projection.ObservedAt } } },
            _ => original
        };
        var entries = setup.Completion.Entries.ToArray(); entries[1] = altered;
        var invalid = setup.Completion with
        {
            Entries = entries,
            PublicEvidenceSha256 = mismatch == "proof" ? new string('0', 64) : setup.Completion.PublicEvidenceSha256
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(invalid.OperationId));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
        await catalog.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("public-native-pending")]
    [DataRow("public-projection-pending")]
    [DataRow("rescan")]
    [DataRow("outcome")]
    public async Task DirectorySettlementCannotCommitAnIncompleteTreeOrPublicResult(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        var invalid = mismatch switch
        {
            "missing" => setup.Completion with { Entries = setup.Completion.Entries.Take(1).ToArray() },
            "duplicate" => setup.Completion with { Entries = [setup.Completion.Entries[0], setup.Completion.Entries[0]] },
            "public-native-pending" => setup.Completion with { PublicResult = setup.Completion.PublicResult with { NativeMoveObserved = false } },
            "public-projection-pending" => setup.Completion with { PublicResult = setup.Completion.PublicResult with { DurableProjectionCommitted = false } },
            "outcome" => setup.Completion with { PublicResult = setup.Completion.PublicResult with { Outcome = (MirrorPulseNamespaceDirectoryMovePublicOutcome)99 } },
            _ => setup.Completion with { PublicResult = setup.Completion.PublicResult with { RequiresFullRescan = true } }
        };
        if (mismatch == "missing")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(invalid));
        else await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(invalid.OperationId));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.Move.RootId));
    }

    private sealed record DirectorySettlementFixture(MoveFixture Move, MirrorPulseNamespaceMemberRegistration Child,
        Guid? ChildMoveId, MirrorPulseNamespaceMemberRegistration[] Members, MirrorPulseNamespaceDirectoryMoveCompletion Completion);

    private static async Task<DirectorySettlementFixture> PrepareDirectorySettlementAsync(MirrorPulseProductCatalog catalog, bool movedChild = false)
    {
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var childBorn = await AddBornAsync(catalog, NestedBirth(setup.Born, "child.txt", false));
        var child = BornMember(childBorn, setup.Member); await catalog.RegisterNamespaceMemberAsync(child);
        var nestedBorn = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", true));
        var nested = BornMember(nestedBorn, setup.Member); await catalog.RegisterNamespaceMemberAsync(nested);
        var grandBorn = await AddBornAsync(catalog, NestedBirth(nestedBorn, "grand.txt", false));
        var grand = BornMember(grandBorn, nested); await catalog.RegisterNamespaceMemberAsync(grand);
        Guid? previous = null;
        var preparedAt = grand.RegisteredAt.AddSeconds(1);
        if (movedChild)
        {
            var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), child.MemberId, childBorn.Protection.ProtectionId,
                childBorn.Birth.RootId, child.LocalObject, childBorn.Plan.ItemId, childBorn.Plan.RemoteId, child.OwnerSid, childBorn.Protection.RoleSid,
                new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, child.RelativePath),
                new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, "Docs/new.txt/renamed.txt"), preparedAt);
            var completed = await CompleteMoveChainStepAsync(catalog, move);
            previous = completed.OperationId; preparedAt = completed.VerifiedAt.AddSeconds(1);
        }
        setup = setup with { Move = setup.Move with { PreparedAt = preparedAt } };
        var completion = await PrepareDirectoryCompletionFactsAsync(catalog, setup);
        return new(setup, child, previous, [setup.Member, child, nested, grand], completion);
    }

    private static async Task<MirrorPulseNamespaceDirectoryMoveCompletion> PrepareDirectoryCompletionFactsAsync(MirrorPulseProductCatalog catalog, MoveFixture setup)
    {
        await StartMoveAsync(catalog, setup);
        var rootNative = MoveObservation(setup); var rootProjection = MoveProjection(rootNative);
        await catalog.RecordNamespaceMemberMoveObservationAsync(rootNative);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(rootProjection);
        var rootCompletion = MoveCompletion(rootNative, rootProjection);
        var snapshot = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId))!;
        var byId = snapshot.Entries.ToDictionary(entry => entry.MemberId);
        var entries = snapshot.Entries.Select(entry =>
        {
            if (entry.Depth == 0) return new MirrorPulseNamespaceDirectoryMoveMemberCompletion(entry.MemberId,
                setup.Move.Target.ParentMemberId, rootNative, rootProjection, rootCompletion);
            var parent = byId[entry.ParentMemberId!.Value];
            var path = setup.Move.Target.RelativePath + entry.RelativePath[setup.Move.Source.RelativePath.Length..];
            var native = new MirrorPulseNamespaceMemberMoveObservation(1, setup.Move.OperationId, entry.Identity.ItemId!.Value,
                entry.Identity.RemoteId!, "native-revision", path, entry.IsDirectory, true, 1,
                new(entry.LocalObject, entry.OwnerSid, Protected, rootNative.Child.ObservedAt),
                new(parent.LocalObject, parent.OwnerSid, Protected, rootNative.Parent.ObservedAt));
            var projection = MoveProjection(native);
            return new(entry.MemberId, parent.MemberId, native, projection, MoveCompletion(native, projection));
        }).ToArray();
        return new(1, setup.Move.OperationId, snapshot.PublicEvidenceSha256,
            new(MirrorPulseNamespaceDirectoryMovePublicOutcome.Projected, true, true, false, entries.Length, rootProjection.ObservedAt), rootCompletion, entries);
    }
}
