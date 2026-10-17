using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OnlyTheOriginalRemoteDeleteMayResumeItsRetainedRootFenceAcrossOwners(bool started)
    {
        await using var fixture = await MoveFixture.OpenAsync();
        var instance = fixture.Docs.InstanceId;
        var batch = new CloudRemoteChangeBatch(instance + "/delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, fixture.Move.RemoteId, "deleted-v2", CloudItemKind.File,
                fixture.Child.RelativePath, fixture.Move.ItemId, previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var child = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Child.MemberId))!;
        var parent = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Parent.MemberId))!;
        var intent = new MirrorPulseNamespaceRetirementIntent(2, Guid.NewGuid(), fixture.Child.MemberId, fixture.Docs.RootId,
            fixture.Child.LocalObject, fixture.Child.RelativePath, false, fixture.Child.OwnerSid, child.Context.Protection.RoleSid, DateTimeOffset.UtcNow)
        {
            RemoteEntry = new(1, instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), 1, 0, "delete",
            fixture.Move.ItemId, fixture.Move.RemoteId, "v1", "deleted-v2", "v1", "v1", child, parent,
            "0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16", new(true, true, TimeSpan.FromMinutes(5).Ticks, 2))
        };
        await fixture.Catalog.PrepareNamespaceRetirementAsync(intent);
        if (started) await fixture.Catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, DateTimeOffset.UtcNow, fixture.Move.ItemId);
        for (int owner = 0; owner < 2; owner++)
        {
            var fence = new MirrorPulseRemoteNamespaceFence(fixture.Router, fixture.Catalog, allowOriginalMoveRecovery: true);
            Assert.IsFalse(await fence.CanPollAsync(fixture.Docs, default));
            Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            await fence.EnsureApplyAllowedAsync(instance, batch, default);
            var altered = new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, batch.Changes, new byte[] { 99 });
            Assert.IsFalse(await fence.CanReplayAsync(fixture.Docs, altered, default));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fence.EnsureApplyAllowedAsync(instance, altered, default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(instance, batch, default).AsTask());
            Assert.HasCount(1, await fixture.Catalog.ReadPendingNamespaceRemoteRetirementsAsync(instance, batch.BatchId));
            var sibling = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Archive.RootId, "Archive Label");
            Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            Assert.IsFalse(await fence.CanPollAsync(fixture.Archive, default));
            await fixture.Catalog.TransitionManagedRootRenameAsync(sibling.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
            Assert.AreEqual(intent, (await fixture.Catalog.ReadNamespaceRemoteRetirementAsync(instance, batch.BatchId, 0))!.Intent);
            if (owner == 0) await fixture.RestartAsync();
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
