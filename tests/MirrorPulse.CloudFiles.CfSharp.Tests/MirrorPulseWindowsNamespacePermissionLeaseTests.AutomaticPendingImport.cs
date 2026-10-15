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
    public async Task NativeAutomaticStartupPreservesPendingImportWithoutAddingAnInitializerFence()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.pending-import-startup"), InstanceId.New(),
            new("existing", "Existing", "Existing", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await using var execution = new MirrorPulseNamespaceExecutionSession();
        Guid operation = Guid.NewGuid(), parentId = Guid.Empty, itemId = Guid.Empty;
        const string expected = "pending import survives startup with its original identity";
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            string input = Path.Combine(directory, "input.txt"); await File.WriteAllTextAsync(input, expected, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token); SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work),
                    router: router, catalog: catalog, namespaceExecution: execution, namespaceActor: actor,
                    scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                var parent = await runtime.InspectNamespaceMemberAsync("Existing", true, timeout.Token);
                if (owner == 0)
                {
                    Assert.IsTrue(work.IsAdmitted(root.RootId)); parentId = parent.MemberId;
                    var plan = await runtime.PrepareNamespaceImportAsync(operation, parentId, "note.txt", input, timeout.Token);
                    itemId = plan.ItemId; Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Existing", "note.txt")));
                }
                else
                {
                    Assert.AreEqual(parentId, parent.MemberId); Assert.IsFalse(work.IsAdmitted(root.RootId));
                    var pending = runtime.NamespaceStartupResults.Single(); Assert.AreEqual(operation, pending.OperationId);
                    Assert.AreEqual("PendingNamespaceOperationRecoveryRequired", pending.RecoveryCode);
                    Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync(timeout.Token));
                    var imported = await runtime.ExecuteNamespaceImportAsync(operation, timeout.Token);
                    Assert.AreEqual(operation, imported.MemberId); Assert.IsFalse(work.IsAdmitted(root.RootId));
                    Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                    var completed = await runtime.RecoverNamespaceRootAsync(Guid.NewGuid(), root.RootId, timeout.Token);
                    Assert.AreEqual(root.RootId, completed.ScopeRoot); Assert.IsTrue(work.IsAdmitted(root.RootId));
                    var actual = await runtime.InspectNamespaceMemberAsync("Existing/note.txt", false, timeout.Token);
                    Assert.AreEqual(itemId, actual.NativeItemId); Assert.AreEqual(imported.MemberId, actual.MemberId);
                    Assert.AreEqual(expected, await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "Existing", "note.txt"), timeout.Token));
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                    TestContext.WriteLine("PendingImportStartup: twoLibraryOwners=True; sameNamespaceRole=True; originalRetryIdAndItemId=True; " +
                        "noCompetingInitializer=True; actualClosedRootImport=True; completionDoesNotAdmit=True; separateFreshAudit=True; " +
                        "bytesAndNativeIdentity=True; sourceCalls=0; newHostRoleRecovery=False; remoteAcceptance=False.");
                }
                Assert.IsEmpty(worker.Calls);
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
