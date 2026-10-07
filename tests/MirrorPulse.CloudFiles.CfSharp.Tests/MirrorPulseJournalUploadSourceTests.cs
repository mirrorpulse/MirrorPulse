using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseJournalUploadSourceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeDisabledSiblingRootKeepsResidentDataAndJournalWithoutRemoteAccess()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration Registration(string key, string name, RootRegistrationState state) =>
            AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.sibling"), instance,
                new AdapterRootDefinition(key, name, name, false), state, identityScope: RootIdentityScope.InstanceRoot);
        RootRegistration active = Registration("docs", "Docs", RootRegistrationState.Active);
        RootRegistration disabled = Registration("offline", "Offline", RootRegistrationState.Disabled);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [active, disabled]);
        var range = new RejectingRangeTransport();
        Guid heldId = Guid.Empty;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            cloud.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(new MirrorPulseDemandProvider(router, range)).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Offline").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("online.txt", router.CreateFileIdentity(instance, "offline", "online", "v1"), 4)
                            .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()],
                        cancellationToken: timeout.Token);
                    await Assert.ThrowsAsync<IOException>(() => File.ReadAllBytesAsync(
                        Path.Combine(paths.SyncRootPath, "Offline", "online.txt"), timeout.Token));
                    Assert.AreEqual(0, range.Requests);
                    await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Offline", "resident.txt"), "offline edit", timeout.Token);
                    await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "active.txt"), "active edit", timeout.Token);
                }
                Assert.AreEqual("offline edit", await File.ReadAllTextAsync(
                    Path.Combine(paths.SyncRootPath, "Offline", "resident.txt"), timeout.Token));
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                MirrorPulseJournalUploadBatch batch;
                CloudLocalChangeBatch raw;
                do
                {
                    batch = await source.ReadPendingAsync(timeout.Token);
                    raw = await feed.ReadBatchAsync(timeout.Token);
                    if (!raw.Changes.Any(change => change.RelativePath.Replace('\\', '/') == "Offline/resident.txt") ||
                        !batch.ReadyCommands.Any(command => command.RootKey == "docs")) await Task.Delay(20, timeout.Token);
                } while (!raw.Changes.Any(change => change.RelativePath.Replace('\\', '/') == "Offline/resident.txt") ||
                    !batch.ReadyCommands.Any(command => command.RootKey == "docs"));
                Guid actualId = raw.Changes.First(change => change.RelativePath.Replace('\\', '/') == "Offline/resident.txt").OperationId;
                if (run == 0) heldId = actualId;
                else Assert.AreEqual(heldId, actualId);
                Assert.IsFalse(batch.ReadyCommands.Any(command => command.RootKey == "offline"));
                Assert.IsGreaterThan(0, batch.DeferredCount);
                Assert.IsNotNull(await catalog.ReadWorkerRequestAsync(heldId, timeout.Token));
                Assert.AreEqual(0, range.Requests);
                Assert.AreEqual(disabled.RootId, router.GetRegistration(instance, "offline").RootId);
            }
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePersistedUploadConflictStopsAutomaticJournalDispatchAcrossRestart()
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
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        try
        {
            cloud.Register(definition);
            Guid operationId = Guid.Empty;
            for (int run = 0; run < 2; run++)
            {
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
                    .Build();
                await fileSystem.StartAsync();
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync();
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);
                    await File.WriteAllTextAsync(
                        Path.Combine(paths.SyncRootPath, "Documents", "report.txt"), "local content");
                }

                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                MirrorPulseJournalUploadBatch batch = await source.ReadPendingAsync(timeout.Token);
                if (run == 0)
                {
                    MirrorPulseWorkerChangeCommand command = batch.ReadyCommands.First(item =>
                        item.RelativePath == "report.txt");
                    operationId = command.OperationId;
                    await catalog.SaveUploadConflictAsync(new MirrorPulseConflictRecord(
                        operationId, instance, operationId.ToString("D"),
                        "Documents/report.txt", MirrorPulseConflictReason.StaleRemoteRevision,
                        MirrorPulseVersionComparison.Diverged, "base", "changed",
                        DateTimeOffset.UtcNow), timeout.Token);
                    batch = await source.ReadPendingAsync(timeout.Token);
                }

                Assert.IsFalse(batch.ReadyCommands.Any(command => command.OperationId == operationId));
                Assert.IsGreaterThan(0, batch.DeferredCount);
                Assert.IsTrue(await catalog.HasPendingUploadConflictAsync(operationId, timeout.Token));
            }
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeUnacknowledgedJournalSurvivesRestartAndDisabledInstance()
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
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        try
        {
            cloud.Register(definition);
            Guid originalId = Guid.Empty;
            for (int run = 0; run < 3; run++)
            {
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
                    .Build();
                await fileSystem.StartAsync();
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync();
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router);
                }
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
                if (run == 0)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(paths.SyncRootPath, "Documents", "report.txt"), "local content");
                }

                var source = new MirrorPulseJournalUploadSource(
                    feed, router, catalog, _ => run != 1);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                MirrorPulseJournalUploadBatch batch = await source.ReadPendingAsync(timeout.Token);
                CloudLocalChangeBatch raw = await feed.ReadBatchAsync(timeout.Token);
                Assert.IsFalse(raw.Changes.Any(change => change.RelativePath == "Documents"));
                Assert.IsFalse(batch.RequiresFullRescan);
                if (run == 0)
                {
                    Assert.IsNotEmpty(batch.ReadyCommands);
                    MirrorPulseWorkerChangeCommand command = batch.ReadyCommands[0];
                    Assert.AreEqual(Path.Combine(paths.SyncRootPath, "Documents", "report.txt"),
                        router.ResolveUploadPath(command.InstanceId, command.RootKey,
                            command.RelativePath));
                    originalId = command.OperationId;
                    Assert.AreNotEqual(Guid.Empty, originalId);
                }
                else if (run == 1)
                {
                    Assert.IsEmpty(batch.ReadyCommands);
                    Assert.IsGreaterThan(0, batch.DeferredCount);
                    MirrorPulseWorkerRequestRecord? recorded = await catalog.ReadWorkerRequestAsync(originalId);
                    Assert.IsNotNull(recorded);
                    Assert.AreEqual(instance, recorded.InstanceId);
                }
                else
                {
                    Assert.IsNotEmpty(batch.ReadyCommands);
                    Assert.AreEqual(originalId, batch.ReadyCommands[0].OperationId);
                    Assert.AreEqual(0, batch.DeferredCount);
                }
            }
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class RejectingRangeTransport : IMirrorPulseWorkerRangeTransport
    {
        public int Requests { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new AssertFailedException("A disabled root must not request remote bytes.");
        }
    }
}
