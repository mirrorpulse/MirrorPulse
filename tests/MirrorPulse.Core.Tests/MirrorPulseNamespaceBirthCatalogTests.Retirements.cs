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
            setup.Member.RelativePath, setup.Member.IsDirectory, setup.Member.OwnerSid, DateTimeOffset.UtcNow);
}
