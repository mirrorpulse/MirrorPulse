using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task RemoteMoveReplayPreservesBatchOwnershipAndOrdinaryExecutionCannotIssueAMove()
    {
        await using var fixture = await MoveFixture.OpenAsync();
        var batch = new CloudRemoteChangeBatch($"{fixture.Docs.InstanceId}/batch", ReadOnlyMemory<byte>.Empty,
            [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, fixture.Move.RemoteId, "incoming", CloudItemKind.File,
                fixture.Move.Target.RelativePath, fixture.Move.ItemId, "previous", fixture.Move.Source.RelativePath)], new byte[] { 1 });
        var intent = fixture.Move with
        {
            Version = 2,
            RemoteEntry = new(1, fixture.Docs.InstanceId, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), 1, 0,
                "move", "previous", "incoming", "original"),
        };
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(intent); await fixture.RestartAsync();
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(state)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
        var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, fixture.Catalog, fixture.Router, execution, actor);
        Assert.AreEqual(intent, await coordinator.PrepareRemoteAsync(intent.OperationId, intent.MemberId,
            intent.Target.ParentMemberId, "moved.txt", fixture.Docs.InstanceId, batch, 0));
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(intent.OperationId).AsTask());
        Assert.AreEqual("A remote move requires its original CfSharp batch owner.", refused.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.PrepareAsync(intent.OperationId,
            intent.MemberId, intent.Target.ParentMemberId, "moved.txt").AsTask());
        var changedBatch = new CloudRemoteChangeBatch(batch.BatchId, ReadOnlyMemory<byte>.Empty, batch.Changes, new byte[] { 2 });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.PrepareRemoteAsync(intent.OperationId,
            intent.MemberId, intent.Target.ParentMemberId, "moved.txt", fixture.Docs.InstanceId, changedBatch, 0).AsTask());
        Assert.IsNull(await fixture.Catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId));
        Assert.AreEqual(fixture.Child, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "new.txt"));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
