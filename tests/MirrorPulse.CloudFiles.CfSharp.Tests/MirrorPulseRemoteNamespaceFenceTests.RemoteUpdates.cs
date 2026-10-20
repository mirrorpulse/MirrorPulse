using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task OnlyTheOriginalExistingUpdateCanResumeWithoutOpeningOrdinaryPolling(bool started)
    {
        await using var fixture = await MoveFixture.OpenAsync(); var instance = fixture.Docs.InstanceId;
        var batch = new CloudRemoteChangeBatch(instance + "/update", new byte[] { 1 },
            [new("update", CloudRemoteChangeKind.FileUpsert, fixture.Move.RemoteId, "v2", CloudItemKind.File, fixture.Child.RelativePath,
                fixture.Move.ItemId, previousRemoteRevision: "v1", length: 4, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(),
                cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var child = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Child.MemberId))!;
        var parent = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Parent.MemberId))!;
        DateTimeOffset prepared = DateTimeOffset.UtcNow;
        var plan = new MirrorPulseNamespaceRemoteUpdatePlan(1, Guid.NewGuid(), instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span),
            1, 0, "update", MirrorPulseRemoteUpdateCoordinator.ChangeFingerprint(batch.Changes[0]), fixture.Move.ItemId, fixture.Move.RemoteId,
            "v1", "v2", "v1", "v1", true, child, parent, child.Context.Verification with { ObservedAt = prepared },
            parent.Context.Verification with { ObservedAt = prepared }, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion,
            MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit, new(true, true, TimeSpan.FromMinutes(5).Ticks, 2), prepared);
        await fixture.Catalog.PrepareNamespaceRemoteUpdateAsync(plan);
        if (started) await fixture.Catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, DateTimeOffset.UtcNow);
        for (int owner = 0; owner < 2; owner++)
        {
            var fence = new MirrorPulseRemoteNamespaceFence(fixture.Router, fixture.Catalog, allowOriginalMoveRecovery: true);
            Assert.IsFalse(await fence.CanPollAsync(fixture.Docs, default)); Assert.IsTrue(await fence.CanPollAsync(fixture.Archive, default));
            Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            await fence.EnsureApplyAllowedAsync(instance, batch, default);
            var altered = new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, batch.Changes, new byte[] { 99 });
            Assert.IsFalse(await fence.CanReplayAsync(fixture.Docs, altered, default));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fence.EnsureApplyAllowedAsync(instance, altered, default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(instance, batch, default).AsTask());
            var pending = await fixture.Catalog.ReadNamespacePendingOperationsAsync(fixture.Docs.RootId);
            Assert.HasCount(1, pending); Assert.AreEqual(MirrorPulseNamespacePendingOperationKind.RemoteUpdate, pending[0].Kind);
            Assert.AreEqual(plan, (await fixture.Catalog.ReadPendingNamespaceRemoteUpdatesAsync(fixture.Docs.RootId)).Single().Plan);
            if (owner == 0) await fixture.RestartAsync();
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
