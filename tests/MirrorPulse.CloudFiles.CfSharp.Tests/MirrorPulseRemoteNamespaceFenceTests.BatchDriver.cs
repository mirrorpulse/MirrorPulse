using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OnlyTheOriginalRemoteMoveCanReachRecoveryWithoutOpeningOrdinaryPolling(bool started)
    {
        await using var fixture = await MoveFixture.OpenAsync();
        var batch = OriginalMoveBatch(fixture);
        var intent = OriginalMoveIntent(fixture, batch);
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(intent);
        if (started) await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, DateTimeOffset.UtcNow));
        for (int owner = 0; owner < 2; owner++)
        {
            var fence = new MirrorPulseRemoteNamespaceFence(fixture.Router, fixture.Catalog, allowOriginalMoveRecovery: true);
            Assert.IsFalse(await fence.CanPollAsync(fixture.Docs, default));
            Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            await fence.EnsureApplyAllowedAsync(fixture.Docs.InstanceId, batch, default);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(fixture.Docs.InstanceId, batch, default).AsTask());
            var altered = new CloudRemoteChangeBatch(batch.BatchId, batch.InitialCursor, batch.Changes, new byte[] { 2 });
            Assert.IsFalse(await fence.CanReplayAsync(fixture.Docs, altered, default));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fence.EnsureApplyAllowedAsync(fixture.Docs.InstanceId, altered, default).AsTask());
            // Recovery in a sibling root remains fenced without taking this original entry's admission.
            var label = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Archive.RootId, "Archive Label");
            Assert.IsTrue(await fence.CanReplayAsync(fixture.Docs, batch, default));
            Assert.IsFalse(await fence.CanPollAsync(fixture.Archive, default));
            await fixture.Catalog.TransitionManagedRootRenameAsync(label.OperationId,
                MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
            Assert.AreEqual(intent, await fixture.Catalog.ReadPendingNamespaceMemberMoveAsync(fixture.Docs.RootId));
            if (owner == 0) await fixture.RestartAsync();
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [SupportedOSPlatform("windows10.0.26100")]
    [DataRow("started")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("change")]
    [DataRow("missing-admission")]
    [DataRow("missing-start")]
    [DataRow("cursor")]
    public async Task ManagedBatchDriverRefusesUncertainOrInconsistentProgressBeforeNativeAccess(string boundary)
    {
        await using var fixture = await MoveFixture.OpenAsync();
        var batch = OriginalMoveBatch(fixture); var instance = fixture.Docs.InstanceId;
        var intent = OriginalMoveIntent(fixture, batch);
        if (boundary != "missing-admission") await fixture.Catalog.PrepareNamespaceMemberMoveAsync(intent);
        if (boundary == "started") await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, DateTimeOffset.UtcNow));
        Directory.CreateDirectory(fixture.Paths.SyncRootPath); var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
        await using var store = await state.OpenAsync(new(fixture.Paths.SyncRootPath));
        int applied = boundary is "change" or "missing-admission" or "missing-start" ? 1 : 0;
        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.RemoteBatches.UpsertAsync(new(batch.BatchId,
                applied == 1 ? batch.FinalCursor.Span : batch.InitialCursor.Span, applied, boundary == "count" ? 2 : 1,
                applied == 1 ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying, [], DateTimeOffset.UtcNow,
                boundary == "fingerprint" ? new byte[32] : batch.Fingerprint, applied == 0 ? null : boundary == "change" ? "different" : "move"));
            if (boundary == "cursor") await transaction.Checkpoints.UpsertAsync(new(
                MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instance), new byte[] { 99 }, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(state)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
        var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, fixture.Catalog, fixture.Router, execution, actor);
        var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
        if (boundary == "started")
        {
            var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(instance, batch).AsTask());
            StringAssert.Contains(refused.Message, "outcome recovery");
        }
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.ApplyAsync(instance, batch).AsTask());
        Assert.AreEqual(boundary == "started", await fixture.Catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId) is not null);
        Assert.IsNull(await fixture.Catalog.ReadNamespaceMemberMoveCompletionAsync(intent.OperationId));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
        var cursor = await coordinator.ReadCursorAsync(instance);
        if (boundary == "cursor") CollectionAssert.AreEqual(new byte[] { 99 }, cursor!.Value.ToArray());
        else Assert.IsNull(cursor);
    }

    private static CloudRemoteChangeBatch OriginalMoveBatch(MoveFixture fixture) => new($"{fixture.Docs.InstanceId}/batch", ReadOnlyMemory<byte>.Empty,
        [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, fixture.Move.RemoteId, "incoming", CloudItemKind.File,
            fixture.Move.Target.RelativePath, fixture.Move.ItemId, "previous", fixture.Move.Source.RelativePath)], new byte[] { 1 });

    private static MirrorPulseNamespaceMemberMoveIntent OriginalMoveIntent(MoveFixture fixture, CloudRemoteChangeBatch batch) =>
        fixture.Move with
        {
            Version = 2,
            RemoteEntry = new(1, fixture.Docs.InstanceId, batch.BatchId,
            Convert.ToHexString(batch.Fingerprint.Span), 1, 0, "move", "previous", "incoming", "original")
        };
}
