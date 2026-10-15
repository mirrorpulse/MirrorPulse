using System.Security.Principal;
using CfSharp;
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
    public async Task NativeRuntimeLibraryRotationWaitsForControlledImportInAnotherRoot()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var roots = AdapterRootRegistrationMapper.MapAll(AdapterId.Parse("example.controlled-operation-drain"), InstanceId.New(),
            [new("import", "Import", "Import", false), new("recovery", "Recovery", "Recovery", false)],
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot).ToArray();
        RootRegistration importingRoot = roots.Single(root => root.DirectoryName == "Import"), recoveryRoot = roots.Single(root => root.DirectoryName == "Recovery");
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pause = new PopulationCommitPause(Guid.NewGuid(), timeout.Token);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            string inputPath = Path.Combine(directory, "input.txt"); const string expected = "controlled import retains its library owner";
            await File.WriteAllTextAsync(inputPath, expected, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                (await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, user.User!);
                foreach (var root in roots)
                {
                    await setup.GetDirectory(root.DirectoryName).SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                    SetProtectedFixtureUserOwner(setup.GetDirectory(root.DirectoryName), user.User!);
                }
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler(); await using var admission = new MirrorPulseNamespaceWorkAdmission(roots.Select(root => root.RootId));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: admission)
            { StateStoreDecorator = inner => new PausingPopulationFactory(inner, pause) };
            await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
            var initialized = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), timeout.Token);
            var parent = (await catalog.ReadNamespaceMemberTreeAsync(initialized.AnchorMemberId, timeout.Token)).Single(member => member.RelativePath == "Import");
            Guid importOperation = Guid.NewGuid(), recoveryOperation = Guid.NewGuid();
            var plan = await runtime.PrepareNamespaceImportAsync(importOperation, parent.MemberId, "retained.txt", inputPath, timeout.Token);
            pause.ItemId = plan.ItemId; pause.Armed = true;
            Task<MirrorPulseNamespaceMemberRegistration> importing = runtime.ExecuteNamespaceImportAsync(importOperation, timeout.Token).AsTask();
            try
            {
                await pause.Entered.Task.WaitAsync(timeout.Token);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Import", "retained.txt")));
                Assert.IsTrue((await runtime.ReadNamespaceImportStatusAsync(importOperation, timeout.Token)).Started);
                using (var queued = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                {
                    queued.CancelAfter(TimeSpan.FromSeconds(1));
                    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await runtime.RecoverNamespaceRootAsync(recoveryOperation, recoveryRoot.RootId, queued.Token));
                }
                Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(recoveryOperation, timeout.Token));
                Assert.IsTrue(admission.IsAdmitted(recoveryRoot.RootId)); Assert.IsTrue(admission.IsAdmitted(importingRoot.RootId));
                Assert.IsFalse(pause.StoreDisposed.Task.IsCompleted); Assert.IsFalse(pause.Committed);
                pause.Release.TrySetResult();
                var imported = await importing.WaitAsync(timeout.Token);
                Assert.IsTrue(pause.Committed); Assert.AreEqual(importingRoot.RootId, imported.RootId);
                Assert.IsTrue((await runtime.ReadNamespaceImportStatusAsync(importOperation, timeout.Token)).Registered);
                var recovered = await runtime.RecoverNamespaceRootAsync(recoveryOperation, recoveryRoot.RootId, timeout.Token);
                Assert.AreEqual(recoveryRoot.RootId, recovered.ScopeRoot);
                Assert.IsTrue(pause.StoreDisposed.Task.IsCompleted);
                var actual = await runtime.InspectNamespaceMemberAsync("Import/retained.txt", false, timeout.Token);
                Assert.AreEqual(imported.MemberId, actual.MemberId); Assert.AreEqual(plan.ItemId, actual.NativeItemId);
                Assert.AreEqual(expected, await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Import", "retained.txt"), timeout.Token));
                Assert.IsTrue(admission.IsAdmitted(importingRoot.RootId)); Assert.IsTrue(admission.IsAdmitted(recoveryRoot.RootId));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                TestContext.WriteLine("ControlledOperationDrain: actualRuntime=True; twoRoots=True; pausedOfficialImportCommit=True; " +
                    "queuedOtherRootRecoveryCancelledBeforeAdmission=True; noSpuriousRootFence=True; storeRetainedUntilImportCompleted=True; " +
                    "sameRecoveryIdUsable=True; bytesBindingAndNativeIdentityPreserved=True; defaultHostStartup=False; remoteAcceptance=False.");
            }
            finally { pause.Release.TrySetResult(); await importing.WaitAsync(timeout.Token); }
        }
        finally
        {
            pause.Release.TrySetResult(); registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
