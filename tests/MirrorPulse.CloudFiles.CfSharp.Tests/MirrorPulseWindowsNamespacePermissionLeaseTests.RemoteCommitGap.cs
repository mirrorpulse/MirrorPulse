using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteFileCommitGapRetainsTheOriginalMovedObjectWithoutRedispatch() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, managedBatch: true, failProgressCommit: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteCaseOnlyCommitGapRetainsTheOriginalMovedObjectWithoutRedispatch() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true, managedBatch: true, failProgressCommit: true);

    private static async Task VerifyUncommittedRemoteMoveAsync(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseNamespaceMemberMoveIntent intent, MirrorPulseNamespaceMemberMoveStart originalStart,
        CloudRemoteChangeBatch batch, string sourcePath, string targetPath, CancellationToken token)
    {
        await using (var read = await state.OpenStore.BeginTransactionAsync(token))
        {
            var progress = await read.RemoteBatches.GetAsync(batch.BatchId, token);
            Assert.IsNotNull(progress); Assert.AreEqual(0, progress.AppliedEntryCount);
            Assert.AreEqual(CloudRemoteBatchStatus.Applying, progress.Status);
            CollectionAssert.AreEqual(batch.Fingerprint.ToArray(), progress.Fingerprint.ToArray());
            var item = await read.Items.GetByItemIdAsync(intent.ItemId, token);
            Assert.IsNotNull(item); Assert.AreEqual(targetPath, item.RelativePath.Replace('\\', '/'));
            Assert.AreEqual(batch.Changes[0].RemoteRevision, item.RemoteRevision);
            await read.RollbackAsync(token);
        }
        var target = await fileSystem.GetFile(targetPath).InspectAsync(token);
        Assert.IsTrue(target.Exists); Assert.IsTrue(target.IsPlaceholder);
        var binding = target.LocalBinding; Assert.IsNotNull(binding);
        Assert.AreEqual(intent.LocalObject.VolumeSerialNumber, binding.VolumeSerialNumber);
        Assert.AreEqual(intent.LocalObject.SyncRootFileId, binding.SyncRootFileId);
        Assert.AreEqual(intent.LocalObject.LocalFileId, binding.LocalFileId);
        Assert.AreEqual(CloudContentAvailability.OnlineOnly, target.ContentAvailability);
        Assert.AreEqual(intent.ItemId, CloudPlaceholderIdentity.Decode(target.PlaceholderIdentity.Span).ItemId);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            var names = new List<string>();
            await foreach (var child in fileSystem.GetDirectory("Docs").EnumerateLocalChildrenAsync(cancellationToken: token))
                names.Add(child.RelativePath.Replace('\\', '/'));
            Assert.Contains(targetPath, names); Assert.DoesNotContain(sourcePath, names);
        }
        else Assert.IsFalse((await fileSystem.GetFile(sourcePath).InspectAsync(token)).Exists);
        Assert.AreEqual(originalStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token));
        Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(intent.MemberId, token)).RelativePath);
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, token));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, token));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(intent.OperationId, token));
        Assert.IsNull(await catalog.ReadNamespaceRemoteMoveRejectionAsync(intent.OperationId, token));
        Assert.AreEqual(intent.OperationId, (await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, token))!.OperationId);
        Assert.IsNull(await new MirrorPulseRemoteBatchCoordinator(fileSystem, state).ReadCursorAsync(
            intent.RemoteEntry!.InstanceId, token));
    }
}
