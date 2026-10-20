using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImmutableRegistrationReuseCannotHideDamageInTheNextTransaction(bool damageIndex)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        string assignment = damageIndex ? "local_file_id='00000000-0000-0000-0000-000000000001'" : "fingerprint=zeroblob(32)";
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_members SET {assignment} WHERE member_id='{setup.Member.MemberId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task ImmutableRegistrationReuseCannotRetainThePreviousCurrentPositionOrName()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        var location = await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId);
        Assert.AreEqual(completed.OperationId, location.CurrentPositionOperationId);
        Assert.AreEqual(setup.Move.Target.RelativePath, location.RelativePath);
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        Assert.AreEqual(setup.Parent, await catalog.ReadNamespacePermissionBaselineAsync(setup.Parent.EvidenceId));
    }

    [TestMethod]
    public async Task ImmutableRegistrationReuseCannotHideLostCurrentNameInTheNextTransaction()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); Guid anchor = setup.ParentMember.ParentMemberId!.Value;
        Assert.HasCount(3, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).Members);
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_member_names WHERE member_id='{setup.Member.MemberId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor));
    }
}
