using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ControlledRetirementIsLeafFirstDurableAndReplayedAcrossOwners()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceRetirementIntent intent;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            intent = Retire(setup, Guid.NewGuid());
            var prepared = await first.PrepareNamespaceRetirementAsync(intent);
            Assert.AreEqual(prepared, await first.PrepareNamespaceRetirementAsync(intent));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteNamespaceRetirementAsync(intent.OperationId, DateTimeOffset.UtcNow));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt));
            var start = await first.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt);
            Assert.AreEqual(start, await first.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1)));
            var native = await first.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
            Assert.AreEqual(native, await first.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, native.NativeDeletedAt!.Value));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
            var completed = await first.CompleteNamespaceRetirementAsync(intent.OperationId, native.NativeDeletedAt.Value.AddSeconds(1));
            Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, completed.Phase);
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
            Assert.AreEqual(completed, await first.CompleteNamespaceRetirementAsync(intent.OperationId, completed.CompletedAt!.Value));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var receipt = await second.ReadNamespaceRetirementAsync(intent.OperationId);
        Assert.IsNotNull(receipt); Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, receipt.Phase);
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(setup.Member.MemberId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => second.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task DirectoryRetirementRejectsRegisteredDescendantsAndRootRetirement()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", false));
        var nestedMember = BornMember(nested, setup.Member); await catalog.RegisterNamespaceMemberAsync(nestedMember);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRetirementAsync(Retire(setup, Guid.NewGuid())));
    }

    [TestMethod]
    public async Task NamespaceTreeReadReturnsCurrentMembersAndDepthWithoutMutation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", false));
        var nestedMember = BornMember(nested, setup.Member);
        await catalog.RegisterNamespaceMemberAsync(nestedMember);

        var tree = await catalog.ReadNamespaceMemberTreeAsync(setup.Member.MemberId);
        Assert.HasCount(2, tree);
        Assert.AreEqual(new MirrorPulseNamespaceMemberTreeEntry(setup.Member.MemberId, setup.Member.ParentMemberId,
            setup.Member.RelativePath, true, setup.Member.LocalObject, 0), tree[0]);
        Assert.AreEqual(new MirrorPulseNamespaceMemberTreeEntry(nestedMember.MemberId, nestedMember.ParentMemberId,
            nestedMember.RelativePath, false, nestedMember.LocalObject, 1), tree[1]);
        Assert.AreEqual(nestedMember, await catalog.ReadNamespaceMemberChildAsync(setup.Member.MemberId, "nested"));
    }

    [TestMethod]
    public async Task RetirementBatchSealsTheSubtreeAndFencesTheRootBeforeNativeWork()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", false));
        await catalog.RegisterNamespaceMemberAsync(BornMember(nested, setup.Member));
        Guid operationId = Guid.NewGuid();

        var batch = await catalog.PrepareNamespaceRetirementBatchAsync(operationId, setup.Member.MemberId);
        Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Prepared, batch.Phase);
        Assert.HasCount(2, batch.Entries);
        var replay = await catalog.PrepareNamespaceRetirementBatchAsync(operationId, setup.Member.MemberId);
        Assert.AreEqual(batch.OperationId, replay.OperationId);
        CollectionAssert.AreEqual(batch.Entries.ToArray(), replay.Entries.ToArray());
        var stored = await catalog.ReadNamespaceRetirementBatchAsync(operationId);
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(batch.Entries.ToArray(), stored.Entries.ToArray());
        CollectionAssert.Contains((await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray(), batch.RootId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(
            Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/blocked-by-batch.txt" }));
    }

    [TestMethod]
    public async Task PendingRetirementFencesTheRootUntilMembershipSettlement()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Retire(setup, Guid.NewGuid());
        await catalog.PrepareNamespaceRetirementAsync(intent);
        CollectionAssert.Contains((await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray(), intent.RootId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(
            Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/blocked.txt" }));
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt);
        var native = await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
        await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, native.NativeDeletedAt!.Value.AddSeconds(1));
        CollectionAssert.DoesNotContain((await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray(), intent.RootId);
    }

    [TestMethod]
    [DataRow("foreign-binding")]
    [DataRow("stale-path")]
    [DataRow("foreign-member")]
    public async Task RetirementAdmissionRejectsSubstitutedCurrentFacts(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Retire(setup, Guid.NewGuid()) with
        {
            MemberId = mismatch == "foreign-member" ? setup.ParentMember.MemberId : setup.Member.MemberId,
            LocalObject = mismatch == "foreign-binding" ? setup.Parent.LocalObject : setup.Member.LocalObject,
            RelativePath = mismatch == "stale-path" ? "Docs/old.txt" : setup.Member.RelativePath
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
    }

    private static MirrorPulseNamespaceRetirementIntent Retire(MoveFixture setup, Guid operationId) =>
        new(1, operationId, setup.Member.MemberId, setup.Member.RootId!.Value, setup.Member.LocalObject,
            setup.Member.RelativePath, setup.Member.IsDirectory, setup.Member.OwnerSid, setup.Born.Protection.RoleSid, DateTimeOffset.UtcNow);
}
