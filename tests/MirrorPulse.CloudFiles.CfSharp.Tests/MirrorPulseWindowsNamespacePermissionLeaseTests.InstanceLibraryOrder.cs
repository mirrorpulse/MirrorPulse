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
    public Task NativeQueuedControlledImportLeavesTheCurrentLibraryAvailableToEarlierInstanceWork() =>
        RunInstanceLibraryOrderAsync(recoverRoot: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCurrentRootRecoveryLetsEarlierInstanceWorkUseTheLibraryBeforeDraining() =>
        RunInstanceLibraryOrderAsync(recoverRoot: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRuntimeShutdownCancelsQueuedImportWithoutClosingItsSharedInstanceScheduler() =>
        RunInstanceLibraryOrderAsync(recoverRoot: false, stopRuntime: true);

    private async Task RunInstanceLibraryOrderAsync(bool recoverRoot, bool stopRuntime = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.instance-library-order"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using var user = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            string input = Path.Combine(directory, "input.txt"); const string bytes = "queued import keeps the current SDK owner available";
            await File.WriteAllTextAsync(input, bytes, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                await setup.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
                SetProtectedFixtureUserOwner(setup.Root, user.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), user.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler(); await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work);
            await using var runtime = factory.Create(paths); await runtime.StartAsync(deadline.Token);
            var initialized = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), deadline.Token);
            var parent = (await catalog.ReadNamespaceMemberTreeAsync(initialized.AnchorMemberId, deadline.Token))
                .Single(member => member.RelativePath == "Docs");
            Guid operation = Guid.NewGuid();
            await using var earlierInstance = await scheduler.AcquireAsync([root.InstanceId], deadline.Token);
            if (recoverRoot)
            {
                using var earlierWork = work.Enter([root.RootId]);
                var recovering = runtime.RecoverNamespaceRootAsync(operation, root.RootId, deadline.Token).AsTask();
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(5));
                    while (work.IsAdmitted(root.RootId)) await Task.Delay(10, probe.Token);
                    Assert.IsFalse(recovering.IsCompleted);
                    var status = await earlierInstance.RunAsync(root.InstanceId,
                        token => runtime.ReadStatusAsync([root.InstanceId], token), probe.Token);
                    Assert.IsNotNull(status); Assert.IsFalse(work.IsAdmitted(root.RootId));
                    earlierWork.Dispose(); await earlierInstance.DisposeAsync();
                    var recovered = await recovering.WaitAsync(deadline.Token);
                    Assert.AreEqual(initialized.AnchorMemberId, recovered.AnchorMemberId);
                    Assert.AreEqual(parent.MemberId, (await runtime.InspectNamespaceMemberAsync("Docs", true, deadline.Token)).MemberId);
                    Assert.IsTrue(work.IsAdmitted(root.RootId));
                    TestContext.WriteLine("RecoveryLibraryOrder: actualRuntime=True; priorRootWork=True; priorInstanceAdmission=True; " +
                        "officialStatusBeforeDrain=True; currentLibraryReopenedAfterDrain=True; originalMembershipPreserved=True; " +
                        "freshAuditBeforeAdmission=True; defaultHostIntegrated=False.");
                }
                finally { earlierWork.Dispose(); await earlierInstance.DisposeAsync(); await recovering.WaitAsync(deadline.Token); }
                return;
            }
            var preparing = runtime.PrepareNamespaceImportAsync(operation, parent.MemberId, "queued.txt", input, deadline.Token).AsTask();
            if (stopRuntime)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                // Wait for the real private input to be published, then cross the SDK
                // owner gate. Shutdown now targets an instance waiter, rather than
                // racing the earlier catalog lookup or input materialization.
                string staged = Path.Combine(paths.DataRootPath, "state", "import-inputs", operation.ToString("N") + ".bin");
                while (!File.Exists(staged))
                {
                    if (preparing.IsCompleted) await preparing;
                    await Task.Delay(10, probe.Token);
                }
                Assert.IsNotNull(await runtime.ReadStatusAsync([root.InstanceId], probe.Token));
                Assert.IsFalse(preparing.IsCompleted);
                await runtime.DisposeAsync().AsTask().WaitAsync(probe.Token);
                await Assert.ThrowsAsync<OperationCanceledException>(() => preparing);
                Assert.IsNull(await catalog.ReadNamespaceBirthAsync(operation, deadline.Token));
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Docs", "queued.txt")));
                Assert.IsTrue(await earlierInstance.RunAsync(root.InstanceId, _ => ValueTask.FromResult(true), probe.Token));
                Assert.IsTrue(await scheduler.RunAsync(InstanceId.New(), _ => ValueTask.FromResult(true), probe.Token));
                TestContext.WriteLine("ShutdownLibraryOrder: actualRuntime=True; queuedImportCancelled=True; noEarlyBirth=True; " +
                    "sharedInstanceSchedulerStillOwnedByCaller=True; nativeLibraryDrained=True; defaultHostIntegrated=False.");
                return;
            }
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                // A real official-store query needs the current library. The queued import
                // cannot reserve that library while the earlier instance admission is held.
                var status = await runtime.ReadStatusAsync([root.InstanceId], probe.Token);
                Assert.IsNotNull(status); Assert.IsFalse(preparing.IsCompleted);
                Assert.IsNull(await catalog.ReadNamespaceBirthAsync(operation, deadline.Token));
                Assert.IsTrue(work.IsAdmitted(root.RootId));
                await earlierInstance.DisposeAsync();
                var plan = await preparing.WaitAsync(deadline.Token);
                Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(operation, deadline.Token));
                var imported = await runtime.ExecuteNamespaceImportAsync(operation, deadline.Token);
                var actual = await runtime.InspectNamespaceMemberAsync("Docs/queued.txt", false, deadline.Token);
                Assert.AreEqual(imported.MemberId, actual.MemberId); Assert.AreEqual(plan.ItemId, actual.NativeItemId);
                Assert.AreEqual(bytes, await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "queued.txt"), deadline.Token));
                TestContext.WriteLine("InstanceLibraryOrder: actualRuntime=True; sharedInstanceLease=True; queuedImportDoesNotOwnLibrary=True; " +
                    "officialStatusWhileQueued=True; noEarlyBirth=True; importCompletesWithOriginalInput=True; nativeBindingVerified=True; " +
                    "defaultHostIntegrated=False; recoveryOrderingStillPending=True.");
            }
            finally { await earlierInstance.DisposeAsync(); await preparing.WaitAsync(deadline.Token); }
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, user.User!, ["Docs"], ["Docs/queued.txt"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
