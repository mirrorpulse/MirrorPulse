using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CurrentTreeSnapshotRetainsMovedOriginalAndRetiredBirthAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceCurrentTreeSnapshot retained;
        MoveFixture original;
        MirrorPulseNamespaceMemberRegistration retiredMember;
        Guid anchor;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var born = await PrepareMovableMemberAsync(catalog); retiredMember = born.Member;
            anchor = born.ParentMember.ParentMemberId!.Value;
            original = await WithOriginalMovableFileAsync(catalog, born);
            await CompleteMoveChainStepAsync(catalog, original.Move);
            var before = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
            Assert.HasCount(4, before.Members); Assert.IsEmpty(before.RetiredMembers);
            var intent = Retire(born, Guid.NewGuid());
            await catalog.PrepareNamespaceRetirementAsync(intent);
            await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, intent.PreparedAt.AddSeconds(2));
            retained = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
            Assert.HasCount(3, retained.Members); Assert.HasCount(1, retained.RetiredMembers);
            Assert.IsFalse(retained.Members.Any(item => item.Context.Member.MemberId == retiredMember.MemberId));
            Assert.AreEqual(retiredMember.MemberId, retained.RetiredMembers[0].Intent.MemberId);
            var moved = retained.Members.Single(item => item.Context.Member.MemberId == original.Member.MemberId);
            Assert.AreEqual(original.Member, moved.Context.Member);
            Assert.AreEqual("Docs/moved.txt", moved.Context.Location.RelativePath);
            Assert.AreEqual(original.Move.ItemId, moved.Identity.ItemId); Assert.AreEqual(original.Move.RemoteId, moved.Identity.RemoteId);
            Assert.IsEmpty(retained.FencedRoots); Assert.AreNotEqual(before.Fingerprint, retained.Fingerprint);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = await reopened.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
        Assert.AreEqual(retained.Fingerprint, replay.Fingerprint);
        CollectionAssert.AreEqual(retained.Members.ToArray(), replay.Members.ToArray());
        CollectionAssert.AreEqual(retained.RetiredMembers.ToArray(), replay.RetiredMembers.ToArray());
        Assert.AreEqual(retiredMember, await reopened.ReadNamespaceMemberAsync(retiredMember.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task CurrentTreeSnapshotIncludesInitializationAndUnregisteredBirthFencesWithoutGrantingReadiness()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); Guid anchor = setup.ParentMember.ParentMemberId!.Value;
        var readyInventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
        var extra = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/unregistered.txt" });
        var pending = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
        Assert.HasCount(3, pending.Members); CollectionAssert.AreEqual(new[] { setup.Move.RootId }, pending.FencedRoots.ToArray());
        Assert.AreNotEqual(readyInventory.Fingerprint, pending.Fingerprint);
        await catalog.RegisterNamespaceMemberAsync(BornMember(extra, setup.ParentMember));
        Assert.IsEmpty((await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).FencedRoots);
        Guid initialization = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.RootId));
        var admitted = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
        Assert.HasCount(4, admitted.Members); CollectionAssert.AreEqual(new[] { setup.Move.RootId }, admitted.FencedRoots.ToArray());
        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization)).Completion);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentTreeSnapshotRejectsLostChildOrRootNamesAcrossRestart(bool rootName)
    {
        using var fixture = new Fixture(); Guid anchor;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); anchor = setup.ParentMember.ParentMemberId!.Value;
            Assert.HasCount(3, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).Members);
            Guid lost = rootName ? setup.ParentMember.MemberId : setup.Member.MemberId;
            await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_member_names WHERE member_id='{lost:D}';");
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceCurrentTreeSnapshotAsync(anchor));
    }

    [TestMethod]
    public async Task CurrentTreeSnapshotCannotHideDamagedRetirementBehindItsPhaseIndex()
    {
        using var fixture = new Fixture(); Guid anchor;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); anchor = setup.ParentMember.ParentMemberId!.Value;
            var intent = Retire(setup, Guid.NewGuid()); await catalog.PrepareNamespaceRetirementAsync(intent);
            await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, intent.PreparedAt.AddSeconds(2));
            Assert.HasCount(1, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).RetiredMembers);
            // The source-generation expression index rejects malformed JSON during the write.
            // That rejection must leave the original retirement and inventory intact.
            var rejected = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_retirements SET payload=X'00';"));
            Assert.AreEqual(1, rejected.SqliteErrorCode);
            Assert.HasCount(1, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).RetiredMembers);
            // Valid JSON can still contradict the retained canonical bytes and phase index.
            await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_retirements SET payload=X'7B7D';");
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceCurrentTreeSnapshotAsync(anchor));
    }

    [TestMethod]
    public async Task CurrentTreeSnapshotRejectsAManagedRootInPlaceOfTheSyncRootAnchor()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.MemberId));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceCurrentTreeSnapshotAsync(Guid.Empty));
    }
}
