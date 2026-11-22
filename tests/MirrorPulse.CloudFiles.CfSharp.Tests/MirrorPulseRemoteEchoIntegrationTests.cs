using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseRemoteEchoIntegrationTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteMetadataApplicationDoesNotEnterLocalUploadJournal() =>
        VerifyRemoteMetadataJournalAsync(writeLocalAttribute: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteMetadataApplicationPreservesImmediateLocalAttributeWrite() =>
        VerifyRemoteMetadataJournalAsync(writeLocalAttribute: true);

    private static async Task VerifyRemoteMetadataJournalAsync(bool writeLocalAttribute)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
        {
            Assert.Inconclusive("Requires the NativeCloudFiles test environment; run the dedicated verification gate.");
        }

        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]);
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        var instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.local"), instance,
            new AdapterRootDefinition("docs", "Documents", "Documents", false),
            RootRegistrationState.Active);
        try
        {
            cloud.Register(definition);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                .WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
                .Build();
            await fileSystem.StartAsync();
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync();
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);

            CloudItemState item;
            await using (ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync())
            {
                item = await transaction.Items.GetByRelativePathAsync("Documents")
                    ?? throw new InvalidDataException("The root placeholder has no CfSharp item state.");
                await transaction.RollbackAsync();
            }

            string directory = Path.Combine(paths.SyncRootPath, "Documents");
            var metadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().Build();
            var change = new CloudRemoteChange(
                "metadata-1", CloudRemoteChangeKind.MetadataUpdate,
                item.RemoteId, "remote-r1", CloudItemKind.Directory, "Documents",
                item.ItemId, previousRemoteRevision: string.IsNullOrWhiteSpace(item.RemoteRevision)
                    ? null : item.RemoteRevision,
                metadata: metadata, cursorAfter: new byte[] { 1 });
            var batch = new CloudRemoteChangeBatch(
                $"{instance}/metadata", ReadOnlyMemory<byte>.Empty, [change], new byte[] { 1 });
            Assert.IsTrue(CloudRemoteApplyOptions.Default.SuppressLocalEcho);
            CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(batch);
            if (writeLocalAttribute)
            {
                FileAttributes attributes = File.GetAttributes(directory);
                Assert.IsFalse(attributes.HasFlag(FileAttributes.Hidden));
                File.SetAttributes(directory, attributes | FileAttributes.Hidden);
                Assert.IsTrue(File.GetAttributes(directory).HasFlag(FileAttributes.Hidden));
            }

            Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status,
                string.Join("; ", result.Entries.Select(entry => $"{entry.Status}: {entry.Error}")));
            Assert.HasCount(1, result.Entries);
            Assert.AreEqual(CloudRemoteApplyEntryStatus.Applied, result.Entries[0].Status);

            // The independent Windows subscriptions can still deliver after the remote apply returns.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            var scan = await feed.BeginScanAsync();
            Assert.IsTrue(scan.RequiresFullRescan);
            var fenced = await feed.ReadPageAsync(scan, 0, 16);
            Assert.IsTrue(fenced.RequiresFullRescan);
            Assert.IsEmpty(fenced.Changes);

            IReadOnlyList<CloudOperationJournalEntry> originals;
            await using (ICloudStateTransaction journal = await state.OpenStore.BeginTransactionAsync())
            {
                var current = await journal.Items.GetByRelativePathAsync("Documents");
                Assert.IsNotNull(current);
                Assert.AreEqual(item.ItemId, current.ItemId);
                Assert.AreEqual("remote-r1", current.RemoteRevision);
                originals = await journal.Operations.ListAsync(16);
                await journal.RollbackAsync();
            }

            int expectedCount = writeLocalAttribute ? 3 : 2;
            Assert.HasCount(expectedCount, originals);
            var reconcile = new MirrorPulseCfSharpFullRescanAdapter(feed, async token =>
            {
                var snapshot = await fileSystem.GetDirectory("Documents").InspectAsync(token);
                Assert.IsTrue(snapshot.Exists);
                Assert.AreEqual<Guid?>(item.ItemId, snapshot.ItemId);
                Assert.AreEqual(writeLocalAttribute, File.GetAttributes(directory).HasFlag(FileAttributes.Hidden));
                return 1;
            });
            var resolution = await reconcile.HandleAsync(new MirrorPulseLocalChangeSignal(true));
            Assert.IsTrue(resolution.WasRequired);
            Assert.AreEqual(1, resolution.ReconciledEntryCount);

            var resumedScan = await feed.BeginScanAsync();
            var page = await feed.ReadAfterMetadataValidationAsync(resumedScan, 0, 16, CancellationToken.None);
            Assert.IsFalse(page.RequiresFullRescan);
            Assert.IsFalse(page.HasMore);
            Assert.HasCount(expectedCount, page.Changes);
            for (int index = 0; index < originals.Count; index++)
            {
                var observed = page.Changes[index];
                var original = originals[index];
                Assert.AreEqual(CloudLocalChangeKind.MetadataUpdate, observed.Kind);
                Assert.AreEqual(CloudStateOperationKind.MetadataUpdate, original.Kind);
                Assert.AreEqual("Documents", observed.RelativePath);
                Assert.IsNull(observed.ItemId);
                Assert.IsNull(original.ItemId);
                Assert.AreEqual(observed.OperationId, original.OperationId);
                Assert.AreEqual(observed.Sequence, original.Sequence);
            }

            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var uploadSource = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
            var batchAfterReconciliation = await uploadSource.ReadBeforeRequestedRescanAsync(CancellationToken.None);
            Assert.IsFalse(batchAfterReconciliation.RequiresFullRescan);
            Assert.IsEmpty(batchAfterReconciliation.ReadyCommands);
            var outcomes = await catalog.ReadBlockedLocalOperationsAsync();
            Assert.HasCount(expectedCount, outcomes);
            CollectionAssert.AreEquivalent(originals.Select(row => row.OperationId).ToArray(), outcomes.Select(row => row.OperationId).ToArray());
            Assert.IsTrue(outcomes.All(outcome => outcome.Reason == MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange));
            await using var settled = await state.OpenStore.BeginTransactionAsync();
            Assert.IsEmpty(await settled.Operations.ListAsync(16));
            await settled.RollbackAsync();
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
