using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public async Task NativeDisabledRootControlledRetirementRetainsOriginalJournalAcrossFreshRolesWithoutDispatch()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.disabled-retirement"), InstanceId.New(),
            new("existing", "Existing", "Existing", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid fileBirth = Guid.NewGuid(), directoryBirth = Guid.NewGuid(), fileRetirement = Guid.NewGuid(), directoryRetirement = Guid.NewGuid();
        MirrorPulseNamespaceMemberRegistration? firstFile = null, firstDirectory = null;
        string? originalRole = null, birthHistory = null, retirementHistory = null;
        Guid? fileItemId = null, directoryItemId = null;
        Dictionary<Guid, string>? originals = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token); SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                if (owner == 0) originalRole = execution.RoleSid.Value;
                else Assert.AreNotEqual(originalRole, execution.RoleSid.Value);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                DisabledCreationStoreObserver? state = null;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work),
                    uploads: source, stats: source, mutations: source, mayDispatch: _ => false,
                    router: router, catalog: catalog, namespaceExecution: execution, namespaceActor: actor,
                    scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true)
                { StateStoreDecorator = inner => state = new(inner) };
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                var parent = await runtime.InspectNamespaceMemberAsync("Existing", true, timeout.Token);
                var file = await runtime.CreateNamespaceMemberAsync(fileBirth, parent.MemberId, "delete.txt", false, timeout.Token);
                var folder = await runtime.CreateNamespaceMemberAsync(directoryBirth, parent.MemberId, "delete-folder", true, timeout.Token);
                Assert.IsNotNull(state);
                if (owner == 0)
                {
                    firstFile = file; firstDirectory = folder;
                    fileItemId = (await runtime.InspectNamespaceMemberAsync("Existing/delete.txt", false, timeout.Token)).NativeItemId;
                    directoryItemId = (await runtime.InspectNamespaceMemberAsync("Existing/delete-folder", true, timeout.Token)).NativeItemId;
                    Assert.IsNotNull(fileItemId); Assert.IsNotNull(directoryItemId);
                    byte[] bytes = "explicit offline deletion retains its original pending local history"u8.ToArray();
                    await using (var writer = new FileStream(Path.Combine(paths.SyncRootPath, "Existing", "delete.txt"), FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(bytes, timeout.Token); writer.SetLength(bytes.Length); writer.Flush(true);
                    }
                    birthHistory = JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileBirth, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryBirth, timeout.Token) });
                    await using var read = await state.Store.BeginTransactionAsync(timeout.Token);
                    originals = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    Assert.IsGreaterThan(0, originals.Count); await read.RollbackAsync(timeout.Token);
                }
                else
                {
                    Assert.AreEqual(firstFile, file); Assert.AreEqual(firstDirectory, folder);
                    Assert.AreEqual(birthHistory, JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileBirth, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryBirth, timeout.Token) }));
                }
                _ = await runtime.PrepareNamespaceMemberRetirementAsync(fileRetirement, file.MemberId, timeout.Token);
                var retiredFile = await runtime.ExecuteNamespaceMemberRetirementAsync(fileRetirement, timeout.Token);
                _ = await runtime.PrepareNamespaceMemberRetirementAsync(directoryRetirement, folder.MemberId, timeout.Token);
                var retiredDirectory = await runtime.ExecuteNamespaceMemberRetirementAsync(directoryRetirement, timeout.Token);
                Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, retiredFile.Phase);
                Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, retiredDirectory.Phase);
                string completed = JsonSerializer.Serialize(new[] { retiredFile, retiredDirectory });
                if (owner == 0) retirementHistory = completed;
                else Assert.AreEqual(retirementHistory, completed);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceMemberRetirementAsync(
                    fileRetirement, folder.MemberId, timeout.Token).AsTask());
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Existing", "delete.txt")));
                Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Existing", "delete-folder")));
                Assert.IsFalse(work.IsAdmitted(root.RootId)); Assert.IsFalse(work.TryEnter([root.RootId], out var ordinary)); Assert.IsNull(ordinary);
                Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                await using (var read = await state.Store.BeginTransactionAsync(timeout.Token))
                {
                    foreach (var original in originals!)
                    {
                        var retained = await read.Operations.GetAsync(original.Key, timeout.Token);
                        Assert.IsNotNull(retained); Assert.AreEqual(original.Value, DisabledCreationJournalFact(retained));
                    }
                    var pending = await read.Operations.ListAsync(64, timeout.Token);
                    Assert.AreEqual(2, pending.Count(row => row.Kind == CloudStateOperationKind.Delete));
                    Assert.AreEqual(1, pending.Count(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == fileItemId));
                    Assert.AreEqual(1, pending.Count(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == directoryItemId));
                    if (owner == 0) originals = pending.ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    await read.RollbackAsync(timeout.Token);
                }
                Assert.IsEmpty(worker.Calls);
            }
            TestContext.WriteLine("DisabledRetirement: fileAndEmptyDirectoryDeleted=True; originalBirthAndJournalFactsRetained=True; " +
                "actualDisposedRoles=True; completedReplayDoesNotResurrect=True; ordinaryAdmissionClosed=True; sourceCalls=0; remoteAcceptance=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }
}
