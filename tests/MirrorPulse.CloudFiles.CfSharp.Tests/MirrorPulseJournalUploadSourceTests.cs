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
public sealed partial class MirrorPulseJournalUploadSourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeBoundedJournalKeepsDeferredHeadAndLaterActiveOperationsDurable() => RunBoundedJournalFairnessAsync(8);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeJournalWindowYieldsBeforePlanningAndRetainsTheCompleteOriginalBoundary() => RunBoundedJournalFairnessAsync(1, 64);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeJournalFinalAdmissionYieldsWithoutPublishingPartialCommandsOrLosingOriginals() => RunBoundedJournalFairnessAsync(1, 64, 16);

    private async Task RunBoundedJournalFairnessAsync(int scanPagesPerRead, int planningBudget = 2048, int admissionBudget = 2048)
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
            var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true,
                scanPagesPerRead: scanPagesPerRead, planningObservationsPerRead: planningBudget, admissionOperationsPerRead: admissionBudget);
            for (int pass = 0; pass < 4; pass++)
            {
                MirrorPulseJournalUploadBatch batch; int pendingTurns = 0, planningTurns = 0, admissionTurns = 0; long? originalBoundary = null;
                do
                {
                    batch = await source.ReadPendingAsync(timeout.Token);
                    originalBoundary ??= batch.WindowUpperSequence;
                    Assert.AreEqual(originalBoundary.Value, batch.WindowUpperSequence);
                    if (batch.ScanInProgress || batch.PlanningInProgress || batch.AdmissionInProgress)
                    {
                        if (batch.PlanningInProgress) planningTurns++;
                        if (batch.AdmissionInProgress) admissionTurns++;
                        pendingTurns++; Assert.IsEmpty(batch.ReadyCommands); Assert.IsNull(batch.CompleteWindow);
                        Assert.IsFalse(batch.RequiresFullRescan);
                        if (pass == 0 && !batch.AdmissionInProgress) Assert.IsNull(await catalog.ReadWorkerRequestAsync(later, timeout.Token), "A partial scan or classification cannot acquire ordinary dispatch ownership.");
                        await using var retained = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                        Assert.IsNotNull(await retained.Operations.GetAsync(firstDeferred, timeout.Token));
                        Assert.IsNotNull(await retained.Operations.GetAsync(later, timeout.Token));
                        await retained.RollbackAsync(timeout.Token);
                    }
                } while (batch.ScanInProgress || batch.PlanningInProgress || batch.AdmissionInProgress);
                if (admissionBudget == 16)
                {
                    Assert.IsGreaterThan(0, admissionTurns);
                    TestContext.WriteLine($"PublicJournalAdmissionBudget: operationsPerRead=16; admissionTurns={admissionTurns}; originalBoundary={originalBoundary}; noPartialCommands=True; officialOriginalsRetained=True.");
                }
                if (scanPagesPerRead == 1)
                {
                    Assert.IsGreaterThan(0, pendingTurns);
                    TestContext.WriteLine($"PublicJournalWindowBudget: pagesPerRead=1; pageSize=64; pendingTurns={pendingTurns}; originalBoundary={originalBoundary}; noPartialOwnership=True; originalIdsRetained=True.");
                    Assert.IsGreaterThan(0, planningTurns);
                    TestContext.WriteLine($"PublicJournalPlanningBudget: observationsPerRead={planningBudget}; planningTurns={planningTurns}; originalBoundary={originalBoundary}; noPartialOwnership=True; originalIdsRetained=True.");
                }
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
            if (scanPagesPerRead == 1)
            {
                var unfinished = await source.ReadPendingAsync(timeout.Token);
                Assert.IsTrue(unfinished.ScanInProgress); Assert.IsEmpty(unfinished.ReadyCommands);
                Assert.IsNull(unfinished.CompleteWindow);
                TestContext.WriteLine($"PublicJournalWindowRestart: oldFeedUpperSequence={unfinished.WindowUpperSequence}; oldWindowIncomplete=True; noPartialDispatch=True.");
            }
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
            while ((accepted = await catalog.ReadMutationAsync(later, timeout.Token))?.State != MirrorPulseMutationState.Acknowledged ||
                !pump.Health.Healthy)
            {
                try { await Task.Delay(20, timeout.Token); }
                catch (OperationCanceledException)
                {
                    // Recoverable faults use the production backoff. The acceptance
                    // oracle waits for both durable acknowledgement and health;
                    // it neither retries the test nor ignores a terminal fault.
                    TestContext.WriteLine($"PublicJournalAcceptanceTimeout: state={accepted?.State}; health={pump.Health}.");
                    throw;
                }
            }
            Assert.IsTrue(pump.Health.Healthy, "Acceptance requires the production runner to clear all recovered faults.");
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
            string log = Path.Combine(paths.DataRootPath, "logs", "mirrorpulse.log");
            if (File.Exists(log))
                foreach (string line in File.ReadLines(log).TakeLast(30)) TestContext.WriteLine($"PublicJournalPumpDiagnostic: {line}");
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
        Guid rootItemId = router.CreateRootPage().Children.Single().Identity.ItemId;
        try
        {
            cloud.Register(definition);
            Guid originalId = Guid.Empty;
            var observedRootMetadata = new Dictionary<Guid, CloudLocalChange>();
            for (int run = 0; run < 3; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
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

                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => run != 1);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                MirrorPulseJournalUploadBatch batch;
                // A finite snapshot may precede the actual file notification.
                // Keep the original deadline and wait for its durable command,
                // rather than assuming the first notification is that file.
                do
                {
                    batch = await source.ReadPendingAsync(timeout.Token);
                    Assert.IsFalse(batch.RequiresFullRescan);
                    if (batch.ScanInProgress || batch.PlanningInProgress || batch.AdmissionInProgress ||
                        run == 0 && !batch.ReadyCommands.Any(command => command.RelativePath == "report.txt"))
                        await Task.Yield();
                    else break;
                    timeout.Token.ThrowIfCancellationRequested();
                } while (true);
                Assert.AreSame(batch.ReadyCommands, batch.ValidatedSequenceCommands);
                Assert.IsTrue(batch.ReadyCommands.All(command => command.RelativePath == "report.txt" && !command.IsDirectory));
                CloudLocalChangeBatch raw = await feed.ReadBatchAsync(timeout.Token);
                Assert.IsFalse(raw.Changes.Any(change => change.RelativePath == "Documents" && change.Sequence <= batch.WindowUpperSequence));
                // Inspect all actually pending official rows, not only the
                // legacy feed's delivery batch. The scan remains finite even
                // if a newer native notification arrives during its pages.
                var pendingScan = await feed.BeginScanAsync(timeout.Token);
                Assert.IsFalse(pendingScan.RequiresFullRescan);
                long after = 0;
                var rootNotifications = new List<CloudLocalChange>();
                while (true)
                {
                    var page = await feed.ReadPageAsync(pendingScan, after, 64, timeout.Token);
                    Assert.IsFalse(page.RequiresFullRescan);
                    Assert.IsLessThanOrEqualTo(64, page.Changes.Count);
                    long previous = after;
                    foreach (var change in page.Changes)
                    {
                        Assert.IsGreaterThan(previous, change.Sequence);
                        Assert.IsLessThanOrEqualTo(pendingScan.ThroughSequence, change.Sequence);
                        previous = change.Sequence;
                        if (change.RelativePath == "Documents") rootNotifications.Add(change);
                    }
                    Assert.IsGreaterThanOrEqualTo(previous, page.LastScannedSequence);
                    Assert.IsLessThanOrEqualTo(pendingScan.ThroughSequence, page.LastScannedSequence);
                    if (!page.HasMore) { Assert.AreEqual(pendingScan.ThroughSequence, page.LastScannedSequence); break; }
                    Assert.IsGreaterThan(after, page.LastScannedSequence);
                    after = page.LastScannedSequence;
                }
                // Metadata covered by this window must have its unsupported
                // bookkeeping result and be settled. A later notification has
                // no authority to extend that already captured boundary.
                Assert.IsFalse(rootNotifications.Any(change => change.Sequence <= batch.WindowUpperSequence));
                foreach (var change in rootNotifications)
                {
                    Assert.AreEqual(CloudLocalChangeKind.MetadataUpdate, change.Kind);
                    Assert.IsTrue(change.IsDirectory); Assert.AreEqual(rootItemId, change.ItemId);
                    Assert.IsNull(await catalog.ReadWorkerRequestAsync(change.OperationId, timeout.Token),
                        "A future root notification cannot acquire ordinary dispatch ownership.");
                    observedRootMetadata.TryAdd(change.OperationId, change);
                }
                var coveredMetadata = observedRootMetadata.Values.Where(change => change.Sequence <= batch.WindowUpperSequence).ToArray();
                var bookkeeping = await catalog.ReadBlockedLocalOperationsAsync(timeout.Token);
                foreach (var change in coveredMetadata)
                {
                    var result = bookkeeping.Single(item => item.OperationId == change.OperationId);
                    Assert.AreEqual(MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, result.Reason);
                    Assert.AreEqual(instance, result.InstanceId); Assert.AreEqual("Documents", result.RelativePath);
                    Assert.AreEqual(change.ObservedAt, result.ObservedAt);
                }
                if (run == 0)
                {
                    MirrorPulseWorkerChangeCommand command = batch.ReadyCommands.First(item => item.RelativePath == "report.txt");
                    Assert.AreEqual(Path.Combine(paths.SyncRootPath, "Documents", "report.txt"),
                        router.ResolveUploadPath(command.InstanceId, command.RootKey, command.RelativePath));
                    originalId = command.OperationId;
                    Assert.AreNotEqual(Guid.Empty, originalId);
                }
                else if (run == 1)
                {
                    Assert.IsEmpty(batch.ReadyCommands);
                    Assert.IsGreaterThan(0, batch.DeferredCount);
                    MirrorPulseWorkerRequestRecord? recorded = await catalog.ReadWorkerRequestAsync(originalId);
                    Assert.IsNotNull(recorded); Assert.AreEqual(instance, recorded.InstanceId);
                }
                else
                {
                    Assert.IsNotEmpty(batch.ReadyCommands);
                    Assert.AreEqual(originalId, batch.ReadyCommands[0].OperationId);
                    Assert.AreEqual(0, batch.DeferredCount);
                }
                await using (var transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    Assert.IsNotNull(await transaction.Operations.GetAsync(originalId, timeout.Token),
                        "Reading, disabling and reopening owners must not acknowledge the original file.");
                    foreach (var change in rootNotifications)
                    {
                        var pending = await transaction.Operations.GetAsync(change.OperationId, timeout.Token);
                        Assert.IsNotNull(pending); Assert.AreEqual(change.ItemId, pending.ItemId);
                    }
                    foreach (var change in coveredMetadata)
                        Assert.IsNull(await transaction.Operations.GetAsync(change.OperationId, timeout.Token),
                            "A covered metadata original must settle only after its durable unsupported result.");
                    await transaction.RollbackAsync(timeout.Token);
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Boundary = "FiniteOriginalJournalAfterPlanning",
                    Run = run,
                    batch.WindowUpperSequence,
                    OriginalFileStillPending = true,
                    RootOrdinaryDispatch = false,
                    CoveredRootMetadataSettled = true,
                    FutureRootMetadataRetained = rootNotifications.Count,
                    AllPendingOfficialPagesInspected = true,
                    KnownCoveredMetadataResultsVerified = coveredMetadata.Length,
                    OriginalDeadlineSeconds = 15
                }));
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
