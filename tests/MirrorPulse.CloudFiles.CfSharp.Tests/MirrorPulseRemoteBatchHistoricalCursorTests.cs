using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseRemoteBatchHistoricalCursorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AppliedHistoricalSdkReceiptCannotReplaceALaterProductCursor(bool rootScoped)
    {
        string parent = Path.Combine(Path.GetTempPath(), "MirrorPulse-historical-cursor-tests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.historical-cursor"), instance,
            new("local", "Local", "Local", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        byte[] initial = Enumerable.Repeat((byte)1, 32).ToArray();
        byte[] final = Enumerable.Repeat((byte)2, 32).ToArray();
        byte[] later = Enumerable.Repeat((byte)3, 32).ToArray();
        string id = rootScoped ? MirrorPulseRemotePollBatchScope.Prefix(root) + Convert.ToHexString(initial) + "/" + Convert.ToHexString(final)
            : instance + "/original-completed";
        var batch = new CloudRemoteChangeBatch(id, initial, [], final);
        string checkpoint = rootScoped ? MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instance, root.RootId)
            : MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instance);
        if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The cursor fixture cleanup escaped its owned parent.");
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var store = await state.OpenAsync(new(paths.SyncRootPath));
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await using (var seed = await store.BeginTransactionAsync())
            {
                await seed.RemoteBatches.UpsertAsync(new(batch.BatchId, final, 0, 0, CloudRemoteBatchStatus.Applied,
                    [], DateTimeOffset.UtcNow, batch.Fingerprint, null));
                await seed.Checkpoints.UpsertAsync(new(checkpoint, later, DateTimeOffset.UtcNow));
                await seed.CommitAsync();
            }
            var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state);
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.ApplyAsync(instance, batch).AsTask());
            Assert.AreEqual("The Adapter batch does not follow its durable CfSharp cursor.", failure.Message);
            CollectionAssert.AreEqual(later, (rootScoped ? await coordinator.ReadCursorAsync(instance, root.RootId)
                : await coordinator.ReadCursorAsync(instance))!.Value.ToArray());
            await using var observed = await store.BeginTransactionAsync();
            var original = (await observed.RemoteBatches.GetAsync(batch.BatchId))!;
            Assert.AreEqual(CloudRemoteBatchStatus.Applied, original.Status);
            CollectionAssert.AreEqual(final, original.Cursor.ToArray());
            CollectionAssert.AreEqual(batch.Fingerprint.ToArray(), original.Fingerprint.ToArray());
            await observed.RollbackAsync();
            Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
