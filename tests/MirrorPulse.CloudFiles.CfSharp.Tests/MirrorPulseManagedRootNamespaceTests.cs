using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseManagedRootNamespaceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRootDeleteProtectionRetainsEntryAfterChildFirstRecursiveDelete()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.delete-probe"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(new NamespaceProbeProvider(router)).Build();
            await fileSystem.StartAsync(timeout.Token);
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(timeout.Token);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
            string managed = Path.Combine(paths.SyncRootPath, "Docs");
            CloudItemSnapshot before = await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token);
            Exception emptyFailure = await DeniedAsync(() => Directory.Delete(managed));
            Assert.IsTrue(Directory.Exists(managed));
            string child = Path.Combine(managed, "unsent.txt");
            await File.WriteAllTextAsync(child, "unsent resident data", timeout.Token);
            Exception recursiveFailure = await DeniedAsync(() => Directory.Delete(managed, true));
            CloudItemSnapshot after = await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token);
            Assert.IsTrue(after.Exists && after.IsPlaceholder);
            CollectionAssert.AreEqual(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
            TestContext.WriteLine($"RootDeleteProbe: emptyRootProtected=True; recursiveRootProtected=True; residentChildRetained={File.Exists(child)}; sourceOperations=0; emptyHResult=0x{emptyFailure.HResult:X8}; recursiveHResult=0x{recursiveFailure.HResult:X8}. A recursive caller can remove children before requesting approval for the protected entry directory.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<Exception> DeniedAsync(Action delete)
    {
        try { await Task.Run(delete); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return exception; }
        throw new AssertFailedException("Deletion of the managed entry directory was not protected.");
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeExternalRootRenameExposesPartialStateMoveRecoveryBoundary()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rename-probe"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(new NamespaceProbeProvider()).Build();
            await fileSystem.StartAsync(timeout.Token);
            await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(timeout.Token);
            await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
            CloudPlaceholderIdentity child = router.CreateFileIdentity(instance, "docs", "child", "v1");
            await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                CloudFilePlaceholderSpec.CreateBuilder("child.txt", child, 4).WithInSyncState(true)
                    .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
            Directory.Move(Path.Combine(paths.SyncRootPath, "Docs"), Path.Combine(paths.SyncRootPath, "Renamed"));
            CloudItemState? movedRoot = null;
            CloudItemState? oldChild = null;
            while (movedRoot is null)
            {
                await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                movedRoot = await transaction.Items.GetByRelativePathAsync("Renamed", timeout.Token);
                oldChild = await transaction.Items.GetByRelativePathAsync(Path.Combine("Docs", "child.txt"), timeout.Token);
                await transaction.RollbackAsync(timeout.Token);
                if (movedRoot is null) await Task.Delay(20, timeout.Token);
            }
            Assert.IsNotNull(oldChild);
            Assert.AreEqual(child.ItemId, oldChild.ItemId);
            CloudItemSnapshot nativeRoot = await fileSystem.GetDirectory("Renamed").InspectAsync(timeout.Token);
            Assert.IsTrue(nativeRoot.Exists && nativeRoot.IsPlaceholder);
            Assert.AreEqual(movedRoot.ItemId, CloudPlaceholderIdentity.Decode(nativeRoot.PlaceholderIdentity.Span).ItemId);
            Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Renamed", "child.txt")));
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                fileSystem.GetDirectory("Docs").MoveToAsync(fileSystem.Root, "Renamed", cancellationToken: timeout.Token).AsTask());
            Assert.AreEqual("Durable state already identifies an unrelated item at the destination path.", failure.Message);
            TestContext.WriteLine("CfSharp 0.1.0-preview.3: an external root rename moves the root's journal item, retains child item paths, and prevents public MoveToAsync replay after partial projection. The native rename succeeded; the managed replay reports InvalidOperationException before a native operation.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePendingRootRenameFencesJournalAndRescanAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        AdapterId adapter = AdapterId.Parse("example.root-rename");
        RootRegistration registration = AdapterRootRegistrationMapper.Map(adapter, instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var remote = new RejectingTransport();
        Guid operation = Guid.Empty;
        Guid rename = Guid.Empty;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int run = 0; run < 2; run++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (run == 0)
                {
                    var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
                        new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                        new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                        [new("docs", "Docs", "Docs", false)]);
                    var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
                        new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                    var configured = new AdapterInstance(adapter, installation.InstallId, instance, "Example",
                        new Dictionary<string, string> { ["sourceDirectory"] = Path.Combine(root, "source") }, [],
                        Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"), true,
                        AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
                    await catalog.SaveAdapterTopologyAsync(new([installation], [configured], [registration]), timeout.Token);
                }
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                if (run == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "report.txt"), "retained edit", timeout.Token);
                    MirrorPulseWorkerChangeCommand? command = null;
                    while (command is null)
                    {
                        command = (await source.ReadPendingAsync(timeout.Token)).ReadyCommands.FirstOrDefault(command => command.RelativePath == "report.txt");
                        if (command is null) await Task.Delay(20, timeout.Token);
                    }
                    operation = command.OperationId;
                    rename = (await catalog.PrepareManagedRootRenameAsync(registration.RootId, "My Files", timeout.Token)).OperationId;
                }
                Assert.IsEmpty((await source.ReadPendingAsync(timeout.Token)).ReadyCommands);
                Assert.AreEqual(rename, (await catalog.ReadManagedRootRenamesAsync(timeout.Token)).Single().OperationId);
                var rescan = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog, remote, remote, _ => true);
                Assert.AreEqual(0, await rescan.ReconcileAsync(timeout.Token));
                CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadDeferredRescanRootsAsync(timeout.Token)).ToArray());
                await using (ICloudStateTransaction retained = await state.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    Assert.IsNotNull(await retained.Operations.GetAsync(operation, timeout.Token));
                    await retained.RollbackAsync(timeout.Token);
                }
                Assert.AreEqual("retained edit", await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "report.txt"), timeout.Token));
                Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single().Label);
                if (run == 1)
                {
                    await catalog.TransitionManagedRootRenameAsync(rename, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled, timeout.Token);
                    Assert.IsTrue((await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.OperationId == operation));
                }
                Assert.AreEqual(0, remote.Calls);
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RejectingTransport : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport
    {
        public int Calls { get; private set; }
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("A root with a pending namespace transition must not upload.");
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("A root with a pending namespace transition must not contact its source.");
        }
    }

    // This permissive provider belongs only to the disposable library boundary
    // probe. Product root rename remains guarded by its durable namespace policy.
    private sealed class NamespaceProbeProvider(MirrorPulseRootRouter? rootRouter = null) : ICloudDemandProvider
    {
        public ValueTask<CloudProviderPolicyDecision> ApproveDeleteAsync(CloudProviderDeleteRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(rootRouter is null
                ? CloudProviderPolicyDecision.Deny : MirrorPulseRootNamespacePolicy.ApproveDelete(rootRouter, request.NormalizedPath));
        public ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
        public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(CloudProviderFetchPlaceholdersRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CloudProviderDirectoryPage([]));
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new AssertFailedException("The namespace probe must not hydrate content."));
    }
}
