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
    public async Task NativeRuntimePublishesSourceBirthsThroughTheCurrentLibraryAndAuditsRepeatedPages()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.runtime-source-population"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
                SetProtectedFixtureUserOwner(setup.Root, caller.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
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
                namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: admission);
            await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
            var directoryIdentity = router.CreateRootPage().Children.Single().Identity.Encode();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await provider.FetchChildrenAsync("Docs", directoryIdentity, null, timeout.Token));
            Assert.IsEmpty(worker.Calls);
            var initialized = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), timeout.Token);
            Assert.IsTrue(admission.IsAdmitted(registration.RootId));
            var root = (await catalog.ReadNamespaceMemberTreeAsync(initialized.AnchorMemberId, timeout.Token)).Single(item => item.RelativePath == "Docs");
            var enumerated = await RunSourceConsumerAsync(directory, "enumerate", execution, timeout.Token);
            TestContext.WriteLine($"RuntimePopulationConsumer: names={string.Join(',', enumerated.Names)}; sourcePages={worker.Calls.Count(call => call == "directory")}.");
            CollectionAssert.AreEqual(CallbackDrainExpectedChildren, enumerated.Names);
            var child = await catalog.ReadNamespaceMemberChildAsync(root.MemberId, "cold.bin", timeout.Token);
            Assert.IsNotNull(child); Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, child.Origin);
            var birth = await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token);
            Assert.IsNotNull(birth); Assert.AreEqual(MirrorPulseNamespaceBirthOrigin.RemotePopulation, birth.Origin);
            var first = await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token);
            Assert.IsNotNull(first); Assert.AreEqual(child.LocalObject, first.LocalObject);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
            int pagesBeforeReplay = worker.Calls.Count(call => call == "directory");
            Assert.IsGreaterThanOrEqualTo(1, pagesBeforeReplay);
            var repeated = await provider.FetchChildrenAsync("Docs", directoryIdentity, null, timeout.Token);
            Assert.IsEmpty(repeated.Children); Assert.IsTrue(repeated.IsComplete);
            Assert.AreEqual(child, await catalog.ReadNamespaceMemberChildAsync(root.MemberId, "COLD.BIN", timeout.Token));
            Assert.AreEqual(first, await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token));
            Assert.HasCount(3, await catalog.ReadNamespaceMemberTreeAsync(initialized.AnchorMemberId, timeout.Token));
            Assert.AreEqual(pagesBeforeReplay + 1, worker.Calls.Count(call => call == "directory")); Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(Path.Combine(paths.SyncRootPath, "Docs", "cold.bin")), timeout.Token));
            TestContext.WriteLine("RuntimeSourcePopulation: realRuntime=True; freshLibraryAfterInitialization=True; closedRootNoSource=True; sourceNormalUser=True; actualRemoteBirthBeforeCallbackReturn=True; repeatedPageFreshAudit=True; immutableFirstBinding=True; hydration=False; defaultHostStartup=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }
}
