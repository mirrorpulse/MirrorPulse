using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public async Task NativeDisabledRootControlledReplacementRetainsBytesBackupAndJournalAcrossFreshRolesWithoutDispatch()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.disabled-replacement"), InstanceId.New(),
            new("existing", "Existing", "Existing", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid birth = Guid.NewGuid(), operation = Guid.NewGuid();
        MirrorPulseNamespaceMemberRegistration? firstMember = null;
        Guid? originalItemId = null;
        string? originalRole = null, originalBirth = null, originalIntent = null, originalCompletion = null;
        Dictionary<Guid, string>? originals = null;
        byte[] oldBytes = "complete original offline content retained in the private backup"u8.ToArray();
        byte[] newBytes = "new explicit offline replacement content"u8.ToArray();
        byte[] laterBytes = "later ordinary edit must survive completed replacement replay"u8.ToArray();
        string input = Path.Combine(directory, "input.bin"), freshInput = Path.Combine(directory, "fresh-input.bin");
        string filePath = Path.Combine(paths.SyncRootPath, "Existing", "note.txt");
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await File.WriteAllBytesAsync(input, newBytes, timeout.Token);
            await File.WriteAllBytesAsync(freshInput, newBytes, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token);
                var parent = setup.GetDirectory("Existing");
                (await parent.CreatePlaceholdersAsync([
                    CloudFilePlaceholderSpec.CreateBuilder("cold-target.bin", router.CreateFileIdentity(root.InstanceId, "existing", "cold-target", "v1"), 8)
                        .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build(),
                    CloudFilePlaceholderSpec.CreateBuilder("cold-input.bin", router.CreateFileIdentity(root.InstanceId, "existing", "cold-input", "v1"), 8)
                        .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token)).ThrowIfAnyFailed();
                foreach (CloudItem item in new CloudItem[] { setup.Root, parent, setup.GetFile("Existing/cold-target.bin"), setup.GetFile("Existing/cold-input.bin") })
                    SetProtectedFixtureUserOwner(item, user.User!);
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
                var member = await runtime.CreateNamespaceMemberAsync(birth, parent.MemberId, "note.txt", false, timeout.Token);
                Assert.IsNotNull(state);
                if (owner == 0)
                {
                    firstMember = member;
                    originalItemId = (await runtime.InspectNamespaceMemberAsync("Existing/note.txt", false, timeout.Token)).NativeItemId;
                    Assert.IsNotNull(originalItemId);
                    await WriteOriginalAsync(oldBytes);
                    originalBirth = JsonSerializer.Serialize(await catalog.ReadNamespaceBirthAsync(birth, timeout.Token));
                    await using var read = await state.Store.BeginTransactionAsync(timeout.Token);
                    originals = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    Assert.IsGreaterThan(0, originals.Count); await read.RollbackAsync(timeout.Token);
                }
                else
                {
                    Assert.AreEqual(firstMember, member);
                    Assert.AreEqual(originalBirth, JsonSerializer.Serialize(await catalog.ReadNamespaceBirthAsync(birth, timeout.Token)));
                    CollectionAssert.AreEqual(laterBytes, await File.ReadAllBytesAsync(filePath, timeout.Token));
                    Assert.IsFalse(File.Exists(input));
                }
                MirrorPulseNamespaceReplacementExecution prepared;
                if (owner == 0)
                {
                    var requests = Enumerable.Range(0, 4).Select(_ => runtime.PrepareNamespaceReplacementAsync(
                        operation, member.MemberId, input, timeout.Token).AsTask()).ToArray();
                    prepared = await await Task.WhenAny(requests);
                    File.Delete(input);
                    originalIntent = JsonSerializer.Serialize(prepared.Intent);
                    foreach (var retained in await Task.WhenAll(requests))
                    {
                        Assert.AreEqual(originalIntent, JsonSerializer.Serialize(retained.Intent));
                        Assert.AreEqual(prepared.Action, retained.Action); Assert.AreEqual(prepared.Start, retained.Start);
                    }
                }
                else
                {
                    prepared = await runtime.PrepareNamespaceReplacementAsync(operation, member.MemberId, input, timeout.Token);
                    Assert.AreEqual(originalIntent, JsonSerializer.Serialize(prepared.Intent));
                }
                var completed = await runtime.ExecuteNamespaceReplacementAsync(operation, timeout.Token);
                if (owner == 0) originalCompletion = JsonSerializer.Serialize(completed);
                else Assert.AreEqual(originalCompletion, JsonSerializer.Serialize(completed));
                var status = await runtime.ReadNamespaceReplacementStatusAsync(operation, timeout.Token);
                Assert.IsTrue(status.Completed && status.BackupRecorded && status.ContentRecorded && status.ProtectionRestored);
                Assert.IsFalse(status.RequiresRecovery);
                if (owner == 0) Assert.AreEqual(completed.RescanGeneration, (await catalog.ReadRootRescanRequestsAsync(timeout.Token)).Single().Generation);
                CollectionAssert.AreEqual(owner == 0 ? newBytes : laterBytes, await File.ReadAllBytesAsync(filePath, timeout.Token));
                Assert.AreEqual(originalItemId, (await runtime.InspectNamespaceMemberAsync("Existing/note.txt", false, timeout.Token)).NativeItemId);
                var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operation, timeout.Token);
                Assert.IsNotNull(facts?.Backup);
                await using (var backup = await new MirrorPulseLocalReplacementBackupStore(paths).OpenLeaseAsync(operation, facts.Backup.Content, timeout.Token))
                {
                    using var bytes = new MemoryStream(); await backup.Data.CopyToAsync(bytes, timeout.Token);
                    CollectionAssert.AreEqual(oldBytes, bytes.ToArray());
                }
                var cold = await runtime.InspectNamespaceMemberAsync("Existing/cold-target.bin", false, timeout.Token);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceReplacementAsync(
                    operation, cold.MemberId, freshInput, timeout.Token).AsTask());
                Guid coldTargetOperation = Guid.NewGuid(), coldInputOperation = Guid.NewGuid();
                var coldTarget = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceReplacementAsync(
                    coldTargetOperation, cold.MemberId, freshInput, timeout.Token).AsTask());
                Assert.AreEqual("An offline replacement target must already be resident.", coldTarget.Message);
                var coldInput = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceReplacementAsync(
                    coldInputOperation, member.MemberId, Path.Combine(paths.SyncRootPath, "Existing", "cold-input.bin"), timeout.Token).AsTask());
                Assert.AreEqual("An offline replacement input must already be resident.", coldInput.Message);
                Assert.IsNull(await catalog.ReadNamespaceReplacementExecutionAsync(coldTargetOperation, timeout.Token));
                Assert.IsNull(await catalog.ReadNamespaceReplacementExecutionAsync(coldInputOperation, timeout.Token));
                if (owner == 0) await WriteOriginalAsync(laterBytes);
                await using (var read = await state.Store.BeginTransactionAsync(timeout.Token))
                {
                    foreach (var original in originals!)
                    {
                        var retained = await read.Operations.GetAsync(original.Key, timeout.Token);
                        Assert.IsNotNull(retained); Assert.AreEqual(original.Value, DisabledCreationJournalFact(retained));
                    }
                    if (owner == 0) originals = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    await read.RollbackAsync(timeout.Token);
                }
                Assert.IsFalse(work.IsAdmitted(root.RootId)); Assert.IsFalse(work.TryEnter([root.RootId], out var ordinary)); Assert.IsNull(ordinary);
                Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                Assert.IsEmpty(worker.Calls);
            }
            TestContext.WriteLine("DisabledReplacement: privateOriginalBackup=True; sameNativeItem=True; originalBirthAndJournalFacts=True; " +
                "actualDisposedRoles=True; concurrentOriginalPreparations=4; inputRemovedAfterFirstReply=True; completedReplayPreservesLaterEdit=True; " +
                "coldTargetAndInputRefused=True; ordinaryAdmissionClosed=True; sourceCalls=0; remoteAcceptance=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }

        async Task WriteOriginalAsync(byte[] bytes)
        {
            await using var writer = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.Read);
            await writer.WriteAsync(bytes, timeout.Token); writer.SetLength(bytes.Length); writer.Flush(true);
        }
    }
}
