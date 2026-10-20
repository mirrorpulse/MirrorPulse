using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task HistoricalPermissionReuseCannotReplaceTheNextTransactionsCurrentRoleHead()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.ParentMember.MemberId))!;
        await RotateContextOriginalParentsAsync(catalog, setup, setup.Move.PreparedAt.AddSeconds(5));
        var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.ParentMember.MemberId))!;
        Assert.AreEqual(Role, original.Context.Protection.RoleSid); Assert.AreEqual(RelocatedRole, current.Context.Protection.RoleSid);
        Assert.AreNotEqual(original.Context.Protection.ProtectionId, current.Context.Protection.ProtectionId);
        var historical = (await catalog.ReadNamespacePermissionChangeAsync(setup.ParentProof.OperationId))!;
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, historical.Phase); Assert.AreEqual(Role, historical.Intent.RoleSid);
        Assert.AreEqual(setup.ParentMember, await catalog.ReadNamespaceMemberAsync(setup.ParentMember.MemberId));
    }

    [TestMethod]
    [DataRow("phase")]
    [DataRow("baseline-index")]
    [DataRow("baseline-missing")]
    public async Task VerifiedHistoricalPermissionReuseCannotHideCorruptionInAnotherTransaction(string damaged)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, damaged switch
        {
            "phase" => "UPDATE namespace_permission_changes SET phase=0;",
            "baseline-index" => $"UPDATE namespace_permission_baselines SET local_file_id='00000000-0000-0000-0000-000000000001' WHERE evidence_id='{setup.Parent.EvidenceId:D}';",
            _ => "PRAGMA foreign_keys=OFF; DELETE FROM namespace_permission_baselines;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }
}
