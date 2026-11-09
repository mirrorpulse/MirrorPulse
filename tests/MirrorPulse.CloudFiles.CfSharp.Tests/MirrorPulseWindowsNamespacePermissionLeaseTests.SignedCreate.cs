using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedCreateConflict")]
    public async Task SignedLocalKeepRemoteControlledCreatePreservesActualVersionsAndOriginalBirthAcrossOwners()
    {
        string? package = Environment.GetEnvironmentVariable("MP_SIGNED_LOCAL_PACKAGE");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" || string.IsNullOrWhiteSpace(package))
            Assert.Inconclusive("Requires the disposable native environment and an official signed Local package.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string source = Path.Combine(directory, "source");
        byte[] selected = "actual independently existing signed backend version"u8.ToArray();
        using var caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); bool registered = false;
        Guid commandId = Guid.Empty, rejectedId = Guid.Empty;
        string? rejectedHistory = null, acknowledgementHistory = null, birthHistory = null;
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "created.bin"), selected, token);
            await File.WriteAllTextAsync(Path.Combine(source, "peer.bin"), "unrelated signed peer", token);
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token))
            {
                string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                var installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                    Path.Combine(directory, "installed"), runtime, cancellationToken: token);
                Assert.IsTrue(installed.IsSigned); Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
                string rootKey = installed.Manifest.RootDefinitions.Single().Key;
                var instance = await catalog.CreateInstanceAsync(installed.InstallId, "Docs",
                    new Dictionary<string, string> { ["sourceDirectory"] = source }, [],
                    Path.Combine(directory, "cache", "files"), Path.Combine(directory, "cache", "transfers"),
                    rootLabels: new Dictionary<string, string> { [rootKey] = "Docs" }, cancellationToken: token);
                var topology = await catalog.ReadAdapterTopologyAsync(token); var root = topology.Roots.Single();
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
                await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
                await supervisor.StartAsync(topology);
                while ((await catalog.ReadInstanceRuntimeStateAsync(instance.InstanceId, token))?.Phase != "Connected")
                    await Task.Delay(25, token);
                string selectedRevision = (await supervisor.StatAsync(new(instance.InstanceId, "created.bin", root.UniquenessKey), token))!;
                Assert.IsFalse(string.IsNullOrEmpty(selectedRevision));
                registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 14])); registered = true;
                var state = new MirrorPulseCfSharpStateSession(paths); var provider = new ProtectedPermissionNoSourceProvider();
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await using var role = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler(); await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await role.RunNamespaceOperationAsync(() => fileSystem.StartAsync(token).AsTask());
                Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs") })
                    SetProtectedFixtureUserOwner(item, caller.User!);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using (var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router,
                    role, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), token)) { }
                var startup = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, role, actor,
                    scheduler, permissions, work).StartAsync(token);
                await using var guard = startup.Guard; Assert.IsTrue(startup.Roots.Single().Admitted);
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 }); await feed.StartAsync(token);
                var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                var file = await CreateKeepRemoteOriginalAsync(fileSystem, state, catalog, router, role, actor, token);
                Guid itemId = (await file.InspectAsync(token)).ItemId!.Value;
                await WindowAsync(1); await File.AppendAllTextAsync(file.FullPath, "complete latest local birth bytes", token);
                var window = await WindowAsync(2);
                var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, itemId, window.WindowUpperSequence, new HashSet<Guid>());
                Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.CreateFile, plan.Effect);
                var before = await file.InspectAsync(token); byte[] local = await File.ReadAllBytesAsync(file.FullPath, token);
                var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role).CaptureUploadBindingAsync(file, before, token);
                var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, null,
                    new(local.Length, Convert.ToHexString(SHA256.HashData(local)), binding), token);
                Assert.HasCount(1, execution.Steps);
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() =>
                    new MirrorPulseJournalCoalescingExecutor(catalog, supervisor, supervisor, supervisor, new(supervisor)).ExecuteAsync(plan.PlanId,
                        (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(local, writable: false)), token).AsTask());
                var rejected = (await catalog.ReadMutationAsync(execution.Steps[0].OperationId, token))!;
                Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State); Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, rejected.Intent.Kind);
                Assert.IsNull(rejected.Intent.ExpectedRevision); rejectedId = rejected.Intent.OperationId;
                rejectedHistory = JsonSerializer.Serialize(rejected);
                Guid conflictId = Guid.Empty;
                foreach (var original in plan.Members)
                {
                    var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), instance.InstanceId, original.OperationId.ToString("D"),
                        "Docs/created.bin", MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged,
                        null, selectedRevision, DateTimeOffset.UtcNow);
                    await catalog.SaveUploadConflictAsync(conflict, token); conflictId = conflict.ConflictId;
                }
                var backoff = new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
                var actions = new MirrorPulseUploadConflictActions(catalog, state, feed, backoff, paths, router, supervisor,
                    scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor, namespaceExecution: role,
                    namespaceWork: work, stats: supervisor, remoteRanges: supervisor, remoteDirectories: supervisor);
                var choice = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token); commandId = choice.ResolutionId;
                if ((await catalog.ReadUserCommandAsync(commandId, token))!.State == "pending")
                {
                    await using var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, supervisor, supervisor, state,
                        paths.SyncRootPath, paths.DataRootPath, _ => true, new(feed, state, backoff), mutations: supervisor,
                        ranges: supervisor, directories: supervisor, fileSystem: fileSystem, scheduler: scheduler,
                        namespaceActor: actor, namespaceExecution: role, namespaceWork: work, conflictActions: actions);
                    await pump.StartAsync(token);
                    while ((await catalog.ReadUserCommandAsync(commandId, token))!.State == "pending") await Task.Delay(25, token);
                    Assert.IsTrue(pump.Health.Healthy);
                }
                Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
                choice = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token); Assert.AreEqual(commandId, choice.ResolutionId);
                CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(choice.PreservedPath!, token));
                CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(file.FullPath, token));
                CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(Path.Combine(source, "created.bin"), token));
                var after = await file.InspectAsync(token);
                Assert.AreEqual(before.LocalBinding, after.LocalBinding); Assert.AreEqual(before.ItemId, after.ItemId); Assert.AreEqual(before.RemoteId, after.RemoteId);
                Assert.AreEqual(selectedRevision, after.RemoteRevision); Assert.AreEqual(CloudSynchronizationState.InSync, after.SynchronizationState);
                var birth = await catalog.ReadConflictKeepRemoteOriginalBirthAsync(commandId, token); Assert.IsNotNull(birth);
                Assert.AreEqual(before.ItemId, birth.ItemId); Assert.AreEqual(before.RemoteId, birth.RemoteId); birthHistory = JsonSerializer.Serialize(birth);
                var retained = (await catalog.ReadConflictKeepRemoteAsync(commandId, token))!;
                Assert.AreEqual("created.bin", retained.Intent.RemoteSource.RemoteId); Assert.AreNotEqual(birth.RemoteId, retained.Intent.RemoteSource.RemoteId);
                var ack = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token); Assert.IsNotNull(ack?.Result);
                CollectionAssert.AreEqual(plan.Members.Select(original => original.OperationId).ToArray(), ack.Result.VerifiedMissingOriginals.ToArray());
                acknowledgementHistory = JsonSerializer.Serialize(ack);
                await File.WriteAllTextAsync(Path.Combine(source, "created.bin"), "later independent backend version", token);
                Assert.AreEqual(commandId, (await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token)).ResolutionId);
                Assert.AreEqual("later independent backend version", await File.ReadAllTextAsync(Path.Combine(source, "created.bin"), token));
                Assert.AreEqual("unrelated signed peer", await File.ReadAllTextAsync(Path.Combine(source, "peer.bin"), token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(token)); Assert.AreEqual(0, provider.Reads);
                TestContext.WriteLine("SignedCreateConflict " + JsonSerializer.Serialize(new
                {
                    packageSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package!, token))),
                    commandId,
                    originalOperationIds = plan.Members.Select(original => original.OperationId).ToArray(),
                    selectedRevisionSha256 = HashRevision(selectedRevision),
                    localSha256 = Convert.ToHexString(SHA256.HashData(local)),
                    selectedContentSha256 = Convert.ToHexString(SHA256.HashData(selected)),
                    acknowledgedOriginalCount = ack.Result.VerifiedMissingOriginals.Count,
                    actualSignedWorker = true,
                    fixtureOwnedOriginalEffects = true,
                    defaultHostProcessDispatchedOriginalEffects = false
                }));

                async Task<MirrorPulseJournalUploadBatch> WindowAsync(int count)
                {
                    while (true)
                    {
                        var batch = await journal.ReadPendingAsync(token);
                        Assert.IsFalse(batch.RequiresFullRescan);
                        if (batch.ReadyCommands.Count(command => command.ItemId == itemId) >= count) return batch;
                        await Task.Delay(25, token);
                    }
                }
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            Assert.AreEqual(rejectedHistory, JsonSerializer.Serialize(await reopened.ReadMutationAsync(rejectedId, token)));
            Assert.AreEqual(acknowledgementHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token)));
            Assert.AreEqual(birthHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteOriginalBirthAsync(commandId, token)));
        }
        finally
        {
            if (registered) registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }
}
