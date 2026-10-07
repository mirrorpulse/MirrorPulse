using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseLocalBatchMapperTests
{
    [TestMethod]
    public async Task UnfilteredMetadataRetainsItsUnsupportedResultWithoutAcknowledgingDirectoriesOrBlockingChildren()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-metadata-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.metadata"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        MirrorPulseLocalChangeObservation Change(string path, bool directory, CloudLocalChangeKind kind) =>
            new(Guid.NewGuid(), 1, kind, null, path, null, directory, DateTimeOffset.UtcNow);
        try
        {
            string directory = Path.Combine(paths.SyncRootPath, "Docs", "folder");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "content.txt");
            await File.WriteAllTextAsync(file, "unchanged content");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Archive);
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-2));
            MirrorPulseLocalChangeObservation[] metadata = [Change("Docs", true, CloudLocalChangeKind.MetadataUpdate),
                Change("Docs/folder", true, CloudLocalChangeKind.MetadataUpdate),
                Change("Docs/folder/content.txt", false, CloudLocalChangeKind.MetadataUpdate)];
            MirrorPulseLocalChangeObservation child = Change("Docs/folder/content.txt", false, CloudLocalChangeKind.ContentUpdate);
            MirrorPulseLocalBatchPlan plan = MirrorPulseLocalBatchMapper.MapObservations([.. metadata, child], router);
            Assert.AreEqual(child.OperationId, plan.Commands.Single().OperationId);
            Assert.IsEmpty(plan.DirectoryMetadataOperationIds);
            Assert.HasCount(3, plan.BlockedOperations!);
            Assert.IsTrue(plan.BlockedOperations!.All(operation => operation.Reason == MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange));
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                foreach (MirrorPulseBlockedLocalOperation operation in plan.BlockedOperations!)
                    await catalog.SaveBlockedLocalOperationAsync(operation);
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var status = new MirrorPulseAppStatusResponse(1, 0, [], [], BlockedLocalOperations: await catalog.ReadBlockedLocalOperationsAsync());
                CollectionAssert.AreEquivalent(metadata.Select(change => change.OperationId).ToArray(),
                    status.BlockedLocalOperations!.Select(operation => operation.OperationId).ToArray());
                Assert.AreEqual("unchanged content", await File.ReadAllTextAsync(file));
            }
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public async Task BlockedRootsAndUnknownPathsDoNotPoisonValidCommandsAndRemainQueryableAfterRestart()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-mapper-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        InstanceId instance = InstanceId.New();
        AdapterId adapter = AdapterId.Parse("example.routing");
        RootRegistration Root(string key, string label) => AdapterRootRegistrationMapper.Map(adapter, instance,
            new AdapterRootDefinition(key, label, label, false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [Root("docs", "Documents"), Root("other", "Other")]);
        MirrorPulseLocalChangeObservation Change(string path, CloudLocalChangeKind kind, bool directory = false, string? previous = null) =>
            new(Guid.NewGuid(), 1, kind, null, path, previous, directory, DateTimeOffset.UtcNow);
        MirrorPulseLocalChangeObservation valid = Change("Documents/report.txt", CloudLocalChangeKind.ContentUpdate);
        MirrorPulseLocalBatchPlan plan = MirrorPulseLocalBatchMapper.MapObservations([
            Change("Documents", CloudLocalChangeKind.Delete, true),
            Change("Unknown/file.txt", CloudLocalChangeKind.Create),
            Change("Documents/moved.txt", CloudLocalChangeKind.Move, previous: "Other/file.txt"),
            Change("Documents/new-folder", CloudLocalChangeKind.Create, true), valid], router);
        Assert.HasCount(2, plan.Commands);
        Assert.AreEqual(valid.OperationId, plan.Commands[1].OperationId);
        Assert.IsTrue(plan.Commands[0].IsDirectory);
        Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, plan.Commands[0].Kind);
        Assert.HasCount(3, plan.BlockedOperations!);
        Assert.IsEmpty(plan.DirectoryMetadataOperationIds);
        CollectionAssert.AreEqual(new[] { MirrorPulseLocalOperationBlockReason.RootReconciliationRequired,
            MirrorPulseLocalOperationBlockReason.UnregisteredPath, MirrorPulseLocalOperationBlockReason.CrossRootMove }, plan.BlockedOperations!.Select(item => item.Reason).ToArray());
        try
        {
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                foreach (MirrorPulseBlockedLocalOperation operation in plan.BlockedOperations!)
                {
                    await catalog.SaveBlockedLocalOperationAsync(operation);
                    await catalog.SaveBlockedLocalOperationAsync(operation);
                }
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                IReadOnlyList<MirrorPulseBlockedLocalOperation> blocked = await catalog.ReadBlockedLocalOperationsAsync();
                Assert.HasCount(3, blocked);
                CollectionAssert.AreEquivalent(plan.BlockedOperations!.Select(item => item.OperationId).ToArray(), blocked.Select(item => item.OperationId).ToArray());
                var status = new MirrorPulseAppStatusResponse(5, 0, [], [], BlockedLocalOperations: blocked);
                Assert.HasCount(3, status.BlockedLocalOperations!);
                await catalog.ClearBlockedLocalOperationAsync(blocked[0].OperationId);
                Assert.HasCount(2, await catalog.ReadBlockedLocalOperationsAsync());
            }
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeJournalBatchRetainsOperationIdentityWithoutContentPayload()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
        {
            Assert.Inconclusive("Requires the NativeCloudFiles test environment; run the dedicated verification gate.");
        }

        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var definition = new MirrorPulseSyncRootDefinition(
            paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]);
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            cloud.Register(definition);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                .WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
                .Build();
            await fileSystem.StartAsync();
            CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync();
            var instance = InstanceId.New();
            RootRegistration registration = AdapterRootRegistrationMapper.Map(
                AdapterId.Parse("example.local"), instance,
                new AdapterRootDefinition("docs", "Documents", "Documents", false),
                RootRegistrationState.Active);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);
            string file = Path.Combine(paths.SyncRootPath, "Documents", "report.txt");
            await File.WriteAllTextAsync(file, "content stays in the local file");

            CloudLocalChangeBatch batch = await WaitForFileChangeAsync(feed, "Documents", "report.txt");
            MirrorPulseLocalBatchPlan plan = MirrorPulseLocalBatchMapper.Map(batch, router);
            Assert.IsFalse(plan.RequiresFullRescan);
            Assert.IsTrue(plan.Commands.Any(command =>
                command.InstanceId == instance &&
                command.RootKey == "docs" &&
                command.RelativePath == "report.txt" &&
                command.OperationId != Guid.Empty));

            CloudLocalChangeBatch repeated = await feed.ReadBatchAsync();
            var repeatedPlan = MirrorPulseLocalBatchMapper.Map(repeated, router);
            CollectionAssert.IsSubsetOf(
                plan.Commands.Select(command => command.OperationId).ToArray(),
                repeatedPlan.Commands.Select(command => command.OperationId).ToArray());
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
            MirrorPulseJournalUploadBatch beforeProjection = await source.ReadPendingAsync();
            MirrorPulseWorkerChangeCommand original = beforeProjection.ReadyCommands.First(command => command.RelativePath == "report.txt");
            await feed.SuppressProviderEchoAsync(CloudStateOperationKind.MetadataUpdate, "Documents/report.txt", DateTimeOffset.UtcNow.AddSeconds(10));
            await fileSystem.GetFile("Documents/report.txt").ConvertToPlaceholderAsync(new CloudPlaceholderIdentity(Guid.NewGuid(), "accepted-remote", "accepted-revision"));
            MirrorPulseJournalUploadBatch afterProjection = await source.ReadPendingAsync();
            MirrorPulseWorkerChangeCommand replay = afterProjection.ReadyCommands.Single(command => command.OperationId == original.OperationId);
            Assert.AreNotEqual(original.ItemId, replay.ItemId);
            Assert.IsFalse((await catalog.ReadBlockedLocalOperationsAsync()).Any(operation =>
                operation.OperationId == original.OperationId && operation.Reason == MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch));
            Assert.IsFalse(typeof(MirrorPulseWorkerChangeCommand).GetProperties()
                .Any(property => property.PropertyType == typeof(byte[])
                    || property.Name.Contains("Payload", StringComparison.Ordinal)));
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CloudLocalChangeBatch> WaitForFileChangeAsync(
        CloudLocalChangeFeed feed,
        string directoryName,
        string fileName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            CloudLocalChangeBatch batch = await feed.ReadBatchAsync(timeout.Token);
            if (batch.Changes.Any(change =>
                change.RelativePath.EndsWith(
                    Path.Combine(directoryName, fileName),
                    StringComparison.OrdinalIgnoreCase)))
            {
                return batch;
            }

            await Task.Delay(100, timeout.Token);
        }
    }
}
