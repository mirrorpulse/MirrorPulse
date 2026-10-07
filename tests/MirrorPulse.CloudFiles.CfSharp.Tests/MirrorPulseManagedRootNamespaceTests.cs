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
}
