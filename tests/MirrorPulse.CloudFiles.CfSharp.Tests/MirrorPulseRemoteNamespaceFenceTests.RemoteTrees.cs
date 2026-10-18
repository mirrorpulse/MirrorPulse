using System.Runtime.Versioning;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task OnlyOriginalDirectoryBatchResumesItsRetainedTreeAcrossCatalogOwners(bool started)
    {
        await using var fixture = await MoveFixture.OpenAsync(directoryChild: true); await fixture.AddTreeChildAsync();
        var instance = fixture.Docs.InstanceId;
        var batch = new CloudRemoteChangeBatch(instance + "/tree-delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, fixture.Move.RemoteId, "deleted-v2", CloudItemKind.Directory,
                fixture.Child.RelativePath, fixture.Move.ItemId, previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var child = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Child.MemberId))!;
        var parent = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Parent.MemberId))!;
        var remote = MirrorPulseRemoteRetirementBatchCoordinator.Reference(instance, batch, 0, CloudRemoteApplyOptions.Default, child, parent, "v1", "v1");
        var entries = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>();
        foreach (var node in await fixture.Catalog.ReadNamespaceMemberTreeAsync(fixture.Child.MemberId))
            entries.Add(new(entries.Count, node.Depth, (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(node.MemberId))!, "v1", "v1"));
        var original = await fixture.Catalog.PrepareNamespaceRemoteRetirementTreeAsync(Guid.NewGuid(), remote, entries, DateTimeOffset.UtcNow);
        if (started) await fixture.Catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, DateTimeOffset.UtcNow);
        for (int owner = 0; owner < 2; owner++)
        {
            var fence = new MirrorPulseRemoteNamespaceFence(fixture.Router, fixture.Catalog, allowOriginalMoveRecovery: true);
            Assert.IsFalse(await fence.CanPollAsync(fixture.Docs, default)); Assert.IsTrue(await fence.CanPollAsync(fixture.Archive, default));
            Assert.IsTrue(await fence.CanPollAsync(fixture.Foreign, default)); Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            await fence.EnsureApplyAllowedAsync(instance, batch, default);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(instance, batch, default).AsTask());
            var altered = new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, batch.Changes, new byte[] { 99 });
            Assert.IsFalse(await fence.CanReplayAsync(fixture.Docs, altered, default));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fence.EnsureApplyAllowedAsync(instance, altered, default).AsTask());
            var retained = (await fixture.Catalog.ReadPendingNamespaceRemoteRetirementTreesAsync(instance, batch.BatchId)).Single();
            MirrorPulseRemoteTreeRetirementCoordinator.RequireOriginal(retained, instance, batch, 0, CloudRemoteApplyOptions.Default);
            Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseNamespaceRetirementCoordinator.RequireControlledBatchReceipt(retained));
            await MirrorPulseRemoteTreeRetirementCoordinator.RequireOriginalCurrentMembershipAsync(fixture.Catalog, retained, default);
            Assert.HasCount(2, retained.Entries);
            var changed = new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor,
                [new("delete", CloudRemoteChangeKind.Delete, remote.RemoteId, "different-v3", CloudItemKind.Directory,
                    fixture.Child.RelativePath, remote.ItemId, previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], batch.FinalCursor);
            Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteTreeRetirementCoordinator.RequireOriginal(retained,
                instance, changed, 0, CloudRemoteApplyOptions.Default));
            if (owner == 0) await fixture.RestartAsync();
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); await using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
            remove.Parameters.AddWithValue("$member", fixture.Child.MemberId.ToString("D")); await remove.ExecuteNonQueryAsync();
        }
        var damaged = (await fixture.Catalog.ReadNamespaceRemoteRetirementTreeAsync(instance, batch.BatchId, 0))!;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            MirrorPulseRemoteTreeRetirementCoordinator.RequireOriginalCurrentMembershipAsync(fixture.Catalog, damaged, default));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
