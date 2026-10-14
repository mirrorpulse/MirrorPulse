using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRuntimeShutdownRetainsAnchorUntilPopulationProjectionDrains()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.runtime-callback-drain"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var identity = router.CreateFileIdentity(registration.InstanceId, registration.UniquenessKey, "cold", "v1");
        var pause = new PopulationCommitPause(identity.ItemId, timeout.Token);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token);
                SetProtectedFixtureUserOwner(setup.Root, user.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), user.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var admission = new MirrorPulseNamespaceWorkAdmission([registration.RootId]);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution)
            { DirectoryPage = new([new("cold", "v1", "file", "cold.bin", 4, null, null, false)], default, true) };
            var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), admission);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: provider, router: router, catalog: catalog,
                namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: admission)
            { StateStoreDecorator = inner => new PausingPopulationFactory(inner, pause) };
            await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
            var initialized = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), timeout.Token);
            Assert.AreEqual(2, initialized.MemberCount); Assert.IsTrue(admission.IsAdmitted(registration.RootId));
            var root = (await catalog.ReadNamespaceMemberTreeAsync(initialized.AnchorMemberId, timeout.Token)).Single(item => item.RelativePath == "Docs");
            pause.Armed = true;
            Task<MirrorPulse.CfSharp.CrashProbe.NamespaceSourceConsumerResult> consumer = RunSourceConsumerAsync(directory, "enumerate", execution, timeout.Token);
            try
            {
                await pause.Entered.Task.WaitAsync(timeout.Token);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Docs", "cold.bin")));
                Assert.AreEqual(1, worker.Calls.Count(call => call == "directory"));
                Task stopping = runtime.DisposeAsync().AsTask();
                Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(pause.StoreDisposed.Task.IsCompleted);
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await runtime.CreateNamespaceMemberAsync(
                    Guid.NewGuid(), root.MemberId, "must-not-exist.txt", false, timeout.Token));
                await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(paths.SyncRootPath, paths.SyncRootPath + ".moved"), timeout.Token));
                Assert.IsTrue(Directory.Exists(paths.SyncRootPath)); Assert.IsFalse(Directory.Exists(paths.SyncRootPath + ".moved"));
                pause.Release.TrySetResult(); await stopping.WaitAsync(timeout.Token);
                Assert.IsTrue(pause.Committed); Assert.IsTrue(pause.StoreDisposed.Task.IsCompleted);
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Docs", "must-not-exist.txt")));
                CollectionAssert.AreEqual(CallbackDrainExpectedChildren, (await consumer.WaitAsync(timeout.Token)).Names);
                var reopenedState = new MirrorPulseCfSharpStateSession(paths);
                await using var reopened = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(reopenedState)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(reopened, execution, timeout.Token);
                await using (var transaction = await reopenedState.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    var persisted = await transaction.Items.GetByItemIdAsync(identity.ItemId, timeout.Token);
                    Assert.IsNotNull(persisted); Assert.AreEqual(identity.RemoteId, persisted.RemoteId); Assert.AreEqual("v1", persisted.RemoteRevision);
                    await transaction.RollbackAsync(timeout.Token);
                }
                var native = await reopened.GetFile("Docs/cold.bin").InspectAsync(timeout.Token);
                Assert.AreEqual(identity, CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span));
                Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
                TestContext.WriteLine("RuntimeShutdownDrain: actualRuntime=True; initializedOriginalTree=True; normalExternalConsumer=True; nativeBeforeOfficialCommit=True; shutdownClosesWork=True; newCreationRejected=True; anchorRetainedDuringCompleteLibraryDrain=True; committedBeforeStoreDisposed=True; originalPublicIdentitySurvivesReopen=True; hydration=False; defaultHostStartup=False; sourceBirthRegistration=False.");
            }
            finally
            {
                pause.Release.TrySetResult(); await runtime.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                try { await consumer.WaitAsync(timeout.Token); }
                catch (Exception error) when (error is OperationCanceledException or IOException or AssertFailedException)
                { TestContext.WriteLine($"Runtime callback fixture consumer cleanup: {error.GetType().Name}."); }
            }
        }
        finally
        {
            pause.Release.TrySetResult();
            if (Directory.Exists(paths.SyncRootPath + ".moved"))
            {
                RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath + ".moved", user.User!);
                Directory.Move(paths.SyncRootPath + ".moved", paths.SyncRootPath);
            }
            registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
