using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeBoundedJournalKeepsDeferredHeadAndLaterActiveOperationsDurable()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration Registration(string key, string name, RootRegistrationState status) =>
            AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.fairness"), instance,
                new AdapterRootDefinition(key, name, name, false), status, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath,
            [Registration("docs", "Docs", RootRegistrationState.Active), Registration("offline", "Offline", RootRegistrationState.Disabled)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            cloud.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await fileSystem.StartAsync(timeout.Token);
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 4 });
            await feed.StartAsync(timeout.Token);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
            for (int index = 0; index < 80; index++)
                await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Offline", $"queued-{index:D2}.txt"), "offline", timeout.Token);
            async Task<Guid> WaitForJournalAsync(string path)
            {
                while (true)
                {
                    await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                    CloudItemState? item = await transaction.Items.GetByRelativePathAsync(path.Replace('/', Path.DirectorySeparatorChar), timeout.Token);
                    IReadOnlyList<CloudOperationJournalEntry> entries = await transaction.Operations.ListAsync(256, timeout.Token);
                    await transaction.RollbackAsync(timeout.Token);
                    CloudOperationJournalEntry? entry = item is null ? null : entries.FirstOrDefault(entry => entry.ItemId == item.ItemId);
                    if (entry is not null) return entry.OperationId;
                    await Task.Delay(20, timeout.Token);
                }
            }
            await WaitForJournalAsync("Offline/queued-79.txt");
            Guid firstDeferred = await WaitForJournalAsync("Offline/queued-00.txt");
            await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "later.txt"), "active", timeout.Token);
            Guid later = await WaitForJournalAsync("Docs/later.txt");
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
            for (int pass = 0; pass < 4; pass++)
            {
                MirrorPulseJournalUploadBatch batch = await source.ReadPendingAsync(timeout.Token);
                Assert.IsTrue(batch.ReadyCommands.Any(command => command.OperationId == later && command.RootKey == "docs"));
                Assert.IsFalse(batch.ReadyCommands.Any(command => command.RootKey == "offline"));
                Assert.IsFalse(batch.RequiresFullRescan);
                Assert.IsGreaterThan(0, batch.DeferredCount);
                Assert.IsFalse((await feed.ReadBatchAsync(timeout.Token)).Changes.Any(change => change.OperationId == later));
            }
            Guid[] originalDeferred;
            await using (ICloudStateTransaction retained = await state.OpenStore.BeginTransactionAsync(timeout.Token))
            {
                Assert.IsNotNull(await retained.Operations.GetAsync(later, timeout.Token));
                Assert.IsNotNull(await retained.Operations.GetAsync(firstDeferred, timeout.Token));
                var offlineItems = new HashSet<Guid>();
                for (int index = 0; index < 80; index++)
                {
                    CloudItemState item = (await retained.Items.GetByRelativePathAsync(Path.Combine("Offline", $"queued-{index:D2}.txt"), timeout.Token))!;
                    Assert.IsNotNull(item);
                    offlineItems.Add(item.ItemId);
                }
                originalDeferred = (await retained.Operations.ListAsync(512, timeout.Token))
                    .Where(entry => entry.ItemId is { } item && offlineItems.Contains(item)).Select(entry => entry.OperationId).ToArray();
                Assert.IsGreaterThanOrEqualTo(80, originalDeferred.Length);
                await retained.RollbackAsync(timeout.Token);
            }
            // Readiness alone is not acceptance. Start the real production pump on
            // a fresh feed and require remote bytes, native confirmation and the
            // original active journal acknowledgement while every deferred ID stays.
            await feed.DisposeAsync();
            string remoteRoot = Path.Combine(root, "accepted-source");
            Directory.CreateDirectory(remoteRoot);
            var remote = new ActiveFileTransport(instance, remoteRoot);
            await using CloudLocalChangeFeed pumpFeed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 4 });
            var completion = new MirrorPulseJournalUploadCompletion(pumpFeed, state,
                new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)));
            await using var pump = new MirrorPulseJournalUploadPump(pumpFeed, router, catalog, remote, remote, state,
                paths.SyncRootPath, paths.DataRootPath, _ => true, completion, ranges: remote, directories: remote, fileSystem: fileSystem);
            await pump.StartAsync(timeout.Token);
            MirrorPulseMutationRecord? accepted;
            while ((accepted = await catalog.ReadMutationAsync(later, timeout.Token))?.State != MirrorPulseMutationState.Acknowledged)
            {
                Assert.IsTrue(pump.Health.Healthy, "The active operation must not hide an acknowledgement fault.");
                await Task.Delay(20, timeout.Token);
            }
            Assert.AreEqual("active", await File.ReadAllTextAsync(Path.Combine(remoteRoot, "later.txt"), timeout.Token));
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(remoteRoot, "later.txt"), timeout.Token))), accepted.Intent.ContentSha256);
            Assert.IsNotNull(accepted.Intent.UploadBinding);
            MirrorPulseContentConfirmationReceipt receipt = (await catalog.ReadContentConfirmationReceiptAsync(later, timeout.Token))!;
            Assert.IsNotNull(receipt);
            Assert.IsTrue(receipt.NativeConfirmationVerified);
            Assert.IsTrue(receipt.DurableProjectionCommitted);
            Assert.IsTrue(receipt.MayAcknowledge);
            Assert.Contains(later, remote.UploadedOperations);
            await using (ICloudStateTransaction after = await state.OpenStore.BeginTransactionAsync(timeout.Token))
            {
                Assert.IsNull(await after.Operations.GetAsync(later, timeout.Token));
                foreach (Guid operation in originalDeferred)
                    Assert.IsNotNull(await after.Operations.GetAsync(operation, timeout.Token), "No disabled original ID may be acknowledged to make progress.");
                await after.RollbackAsync(timeout.Token);
            }
            Assert.AreEqual("offline", await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Offline", "queued-00.txt"), timeout.Token));
            TestContext.WriteLine($"PublicJournalFairAcceptance: deferredFiles=80; deferredOriginalIds={originalDeferred.Length}; batchSize=4; pageSize=64; activeRemoteBytesVerified=True; originalActiveIdAcknowledged=True; nativeConfirmation=True; durableProjection=True; disabledSourceCalls=0; OS={Environment.OSVersion.Version}.");
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
    private sealed class ActiveFileTransport(InstanceId instance, string root) : IMirrorPulseWorkerUploadTransport,
        IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerRangeTransport
    {
        public List<Guid> UploadedOperations { get; } = [];

        private string Resolve(InstanceId requestedInstance, string? rootKey, string path)
        {
            Assert.AreEqual(instance, requestedInstance);
            Assert.AreEqual("docs", rootKey, "The production pump must never contact the disabled sibling source.");
            Assert.IsTrue(path is "later.txt" or "", "The fixture exposes only one active file and its parent.");
            return Path.Combine(root, path);
        }

        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            string path = Resolve(request.InstanceId, request.RootKey, request.NormalizedPath);
            Assert.IsNotNull(request.OperationId);
            Assert.IsNull(request.ExpectedRevision);
            Assert.IsTrue(request.DestinationMustBeAbsent);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await request.Content.CopyToAsync(target, token);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            Assert.AreEqual(request.Length, bytes.LongLength);
            string revision = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.AreEqual(request.ExpectedContentSha256, revision);
            UploadedOperations.Add(request.OperationId.Value);
            return revision;
        }

        public async ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        {
            string path = Resolve(request.InstanceId, request.RootKey, request.NormalizedPath);
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))) : null;
        }

        public async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            _ = Resolve(request.InstanceId, request.RootKey, request.NormalizedPath);
            Assert.IsTrue(request.ContinuationCursor.IsEmpty);
            string path = Path.Combine(root, "later.txt");
            if (!File.Exists(path)) return new([], ReadOnlyMemory<byte>.Empty, true);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            var info = new FileInfo(path);
            return new([new("active:later.txt", Convert.ToHexString(SHA256.HashData(bytes)), "File", "later.txt", bytes.LongLength,
                info.CreationTimeUtc, info.LastWriteTimeUtc, false)], ReadOnlyMemory<byte>.Empty, true);
        }

        public async ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Resolve(request.InstanceId, request.RootKey, request.NormalizedPath), token);
            return new MemoryStream(bytes.AsSpan(checked((int)request.Offset), checked((int)request.Length)).ToArray(), writable: false);
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
