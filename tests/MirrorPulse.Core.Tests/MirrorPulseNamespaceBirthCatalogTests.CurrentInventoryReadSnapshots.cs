using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("NamespaceCurrentInventoryReadSnapshot")]
    public async Task CurrentInventoryReadsRetainWholeAndSelectedGraphsWithoutCatalogWrites()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog);
        var whole = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor);
        var root = setup.Roots[0].RootId!.Value;
        var selected = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, root);
        await ObserveRecoveryReadSqlAsync(catalog, "current-whole-inventory", async () =>
        {
            Assert.AreEqual(SerializeBornOwnerTest(whole), SerializeBornOwnerTest(
                await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor)));
        });
        await ObserveRecoveryReadSqlAsync(catalog, "current-selected-inventory", async () =>
        {
            Assert.AreEqual(SerializeBornOwnerTest(selected), SerializeBornOwnerTest(
                await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, root)));
        });
        Assert.IsNull(whole.ScopeRoot);
        Assert.AreEqual(root, selected.ScopeRoot);
        Assert.IsTrue(selected.Members.All(member => member.Context.Member.RootId is null || member.Context.Member.RootId == root));
        Assert.AreEqual(whole.Members.Count - selected.Members.Count, whole.Members.Count(member =>
            member.Context.Member.RootId is not null && member.Context.Member.RootId != root));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("NamespaceCurrentInventoryReadSnapshot")]
    [DataRow(false, "root-member")]
    [DataRow(true, "root-member")]
    [DataRow(false, "child-member")]
    [DataRow(true, "child-member")]
    [DataRow(false, "root-name")]
    [DataRow(true, "root-name")]
    [DataRow(false, "child-name")]
    [DataRow(true, "child-name")]
    public async Task APreviousInventoryReadCannotHideDamagedCurrentMembersOrNames(bool scoped, string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog);
        var root = setup.Roots[0];
        Task<MirrorPulseNamespaceCurrentTreeSnapshot> ReadAsync() => scoped
            ? catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, root.RootId!.Value)
            : catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor);
        var original = await ReadAsync();
        Assert.AreEqual(SerializeBornOwnerTest(original), SerializeBornOwnerTest(await ReadAsync()));
        var child = original.Members.Single(member => member.Context.Location.ParentMemberId == root.MemberId).Context.Member;
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "root-member" => $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{root.MemberId:D}';",
            "child-member" => $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{child.MemberId:D}';",
            "root-name" => $"DELETE FROM namespace_member_names WHERE member_id='{root.MemberId:D}';",
            "child-name" => $"DELETE FROM namespace_member_names WHERE member_id='{child.MemberId:D}';",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(ReadAsync);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
