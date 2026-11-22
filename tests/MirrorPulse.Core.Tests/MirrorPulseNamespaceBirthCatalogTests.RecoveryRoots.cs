using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task RecoveryRootSnapshotSurvivesStartAndRestartWithoutChangingMoveOrNames()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.HasCount(0, await first.ReadNamespaceRecoveryRootsAsync());
            setup = await PrepareMovableMemberAsync(first);
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
            CollectionAssert.AreEqual(new[] { setup.Move.RootId }, (await first.ReadNamespaceRecoveryRootsAsync()).ToArray());
            await first.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { setup.Move.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.AreEqual(setup.Move, await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second.ReadNamespaceRecoveryRootsAsync(cancellation.Token));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("noncanonical-root")]
    [DataRow("intent-fingerprint")]
    [DataRow("pending-member")]
    [DataRow("reservation")]
    public async Task RecoveryRootSnapshotRejectsDamagedPendingMoveInsteadOfDroppingItsFence(string damage)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { setup = await PrepareMovableMemberAsync(first); await first.PrepareNamespaceMemberMoveAsync(setup.Move); }
        string sql = damage switch
        {
            "noncanonical-root" => "UPDATE namespace_member_move_pending SET root_id='{' || root_id || '}';",
            "intent-fingerprint" => "UPDATE namespace_member_moves SET fingerprint=zeroblob(32);",
            "pending-member" => $"UPDATE namespace_member_move_pending SET member_id='{setup.ParentMember.MemberId:D}';",
            _ => "DELETE FROM namespace_member_move_reservations;"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespaceRecoveryRootsAsync());
        Assert.AreEqual(setup.Member, await current.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }
}
