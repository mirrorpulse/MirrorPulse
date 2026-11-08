using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedPostMove")]
    public async Task SignedLocalKeepRemoteAfterAcknowledgedMovePreservesActualVersionsAndOriginalJournalAcrossOwners()
    {
        string? package = Environment.GetEnvironmentVariable("MP_SIGNED_LOCAL_PACKAGE");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" || string.IsNullOrWhiteSpace(package))
            Assert.Inconclusive("Requires the disposable native environment and an official signed Local package.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string source = Path.Combine(directory, "source");
        byte[] seed = "signed original before movement"u8.ToArray();
        byte[] selected = "signed independently selected content after the original move"u8.ToArray();
        using var caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        bool registered = false;
        Guid commandId = Guid.Empty, moveEffectId = Guid.Empty, rejectedEffectId = Guid.Empty;
        string? movedHistory = null, rejectedHistory = null, ackHistory = null, journalHistory = null;
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "before.bin"), seed, token);
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token))
            {
                string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                var installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                    Path.Combine(directory, "installed"), runtime, cancellationToken: token);
                Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
                Assert.IsTrue(installed.IsSigned); Assert.AreEqual(2, installed.Manifest.Protocol.Minimum);
                Assert.AreEqual(2, installed.Manifest.Protocol.Maximum);
                string rootKey = installed.Manifest.RootDefinitions.Single().Key;
                var instance = await catalog.CreateInstanceAsync(installed.InstallId, "Docs",
                    new Dictionary<string, string> { ["sourceDirectory"] = source }, [],
                    Path.Combine(directory, "cache", "files"), Path.Combine(directory, "cache", "transfers"),
                    rootLabels: new Dictionary<string, string> { [rootKey] = "Docs" }, cancellationToken: token);
                var topology = await catalog.ReadAdapterTopologyAsync(token);
                var root = topology.Roots.Single();
                Assert.AreEqual("Docs", root.DirectoryName);
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
                await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
                await supervisor.StartAsync(topology);
                while ((await catalog.ReadInstanceRuntimeStateAsync(instance.InstanceId, token))?.Phase != "Connected")
                    await Task.Delay(25, token);
                string baseline = (await supervisor.StatAsync(new(instance.InstanceId, "before.bin", root.UniquenessKey), token))!;
                Assert.IsFalse(string.IsNullOrEmpty(baseline));
                registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 14])); registered = true;
                var state = new MirrorPulseCfSharpStateSession(paths);
                var provider = new ProtectedPermissionNoSourceProvider();
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await using var role = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await role.RunNamespaceOperationAsync(() => fileSystem.StartAsync(token).AsTask());
                var file = fileSystem.GetFile("Docs/before.bin");
                Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                await File.WriteAllBytesAsync(file.FullPath, seed, token);
                await file.ConvertToPlaceholderAsync(router.CreateFileIdentity(instance.InstanceId, root.UniquenessKey,
                    "before.bin", baseline), cancellationToken: token);
                foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), file })
                    SetProtectedFixtureUserOwner(item, caller.User!);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using (var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router,
                    role, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), token)) { }
                var startup = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, role, actor,
                    scheduler, permissions, work).StartAsync(token);
                await using var startupGuard = startup.Guard;
                Assert.IsTrue(startup.Roots.Single().Admitted);
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                await feed.StartAsync(token);
                var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                Guid itemId = (await file.InspectAsync(token)).ItemId!.Value;
                Assert.IsTrue(MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, "before.bin",
                    CloudPlaceholderIdentity.Decode((await file.InspectAsync(token)).PlaceholderIdentity.Span)));
                file = await MoveKeepRemoteOriginalAsync(fileSystem, state, catalog, router, role, actor, file, token);
                await ReadWindowAsync(1);
                await File.AppendAllTextAsync(file.FullPath, " latest retained local edit", token);
                var window = await ReadWindowAsync(2);
                var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, itemId,
                    window.WindowUpperSequence, new HashSet<Guid>());
                Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.MoveAndUpdateFile, plan.Effect);
                Assert.HasCount(2, plan.Members);
                byte[] local = await File.ReadAllBytesAsync(file.FullPath, token);
                var before = await file.InspectAsync(token);
                var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role).CaptureUploadBindingAsync(file, before, token);
                var content = new MirrorPulseCoalescingContent(local.Length, Convert.ToHexString(SHA256.HashData(local)), binding);
                var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, baseline, content, token);
                Assert.HasCount(2, execution.Steps);
                string? selectedRevision = null;
                // Change the real backend only after the signed Worker's original Move
                // return is durable. The public executor still owns both original steps
                // and acknowledges the prefix before it prepares the rejected upload.
                var executor = new MirrorPulseJournalCoalescingExecutor(catalog, supervisor, supervisor, supervisor, new(supervisor));
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => executor.ExecuteAsync(plan.PlanId,
                    (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(local, writable: false)),
                    async (accepted, stop) =>
                    {
                        Assert.AreEqual(MirrorPulseWorkerChangeKind.Move, accepted.Intent.Kind);
                        Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, accepted.State); Assert.IsNull(accepted.Intent.UploadBinding);
                        Assert.AreEqual(accepted.AcceptedRevision, await supervisor.StatAsync(new(instance.InstanceId, "original.bin", root.UniquenessKey), stop));
                        Assert.IsFalse(File.Exists(Path.Combine(source, "before.bin")));
                        await catalog.SaveInstanceRuntimeStateAsync(new(instance.InstanceId, "Connected", false, DateTimeOffset.UtcNow), stop);
                        await File.WriteAllBytesAsync(Path.Combine(source, "original.bin"), selected, stop);
                        selectedRevision = await supervisor.StatAsync(new(instance.InstanceId, "original.bin", root.UniquenessKey), stop);
                        Assert.IsFalse(string.IsNullOrEmpty(selectedRevision)); Assert.AreNotEqual(accepted.AcceptedRevision, selectedRevision);
                    }, token).AsTask());
                var moved = await catalog.ReadMutationAsync(execution.Steps[0].OperationId, token);
                Assert.IsNotNull(moved); Assert.AreEqual(MirrorPulseMutationState.Acknowledged, moved.State);
                Assert.IsFalse(string.IsNullOrEmpty(moved.AcceptedRevision));
                Assert.IsFalse(string.IsNullOrEmpty(selectedRevision));
                var rejected = (await catalog.ReadMutationAsync(execution.Steps[1].OperationId, token))!;
                string expectedRevision = moved.AcceptedRevision!;
                Assert.AreEqual(expectedRevision, rejected.Intent.ExpectedRevision);
                Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State);
                Guid conflictId = Guid.Empty;
                foreach (var member in plan.Members)
                {
                    var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), instance.InstanceId, member.OperationId.ToString("D"),
                        "Docs/original.bin", MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged,
                        moved.AcceptedRevision, selectedRevision, DateTimeOffset.UtcNow);
                    await catalog.SaveUploadConflictAsync(conflict, token); conflictId = conflict.ConflictId;
                }
                movedHistory = JsonSerializer.Serialize(moved); rejectedHistory = JsonSerializer.Serialize(rejected);
                moveEffectId = moved.Intent.OperationId; rejectedEffectId = rejected.Intent.OperationId;
                var actions = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
                    paths, router, supervisor, scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor,
                    namespaceExecution: role, namespaceWork: work, stats: supervisor, remoteRanges: supervisor, remoteDirectories: supervisor);
                var decision = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token);
                commandId = decision.ResolutionId;
                while ((await catalog.ReadUserCommandAsync(commandId, token))!.State == "pending")
                {
                    if ((await feed.BeginScanAsync(token)).RequiresFullRescan)
                    {
                        var policy = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog,
                            supervisor, supervisor, _ => true, supervisor, supervisor, supervisor, scheduler: scheduler, namespaceWork: work);
                        await new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync, feed.AcknowledgeFullRescanAsync,
                            (_, _) => ValueTask.CompletedTask).RunAsync(new(true), token);
                    }
                    await Task.Delay(25, token);
                    decision = await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token);
                    Assert.AreEqual(commandId, decision.ResolutionId);
                }
                Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
                CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(decision.PreservedPath!, token));
                CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(file.FullPath, token));
                CollectionAssert.AreEqual(selected, await File.ReadAllBytesAsync(Path.Combine(source, "original.bin"), token));
                var after = await file.InspectAsync(token);
                Assert.AreEqual(before.LocalBinding, after.LocalBinding); Assert.AreEqual(selectedRevision, after.RemoteRevision);
                Assert.AreEqual(CloudSynchronizationState.InSync, after.SynchronizationState);
                var ack = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token);
                Assert.IsNotNull(ack?.Result);
                CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), ack.Result.VerifiedMissingOriginals.ToArray());
                ackHistory = JsonSerializer.Serialize(ack);
                journalHistory = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteJournalAsync(commandId, token));
                Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                Assert.HasCount(0, await catalog.ReadUploadConflictsAsync(cancellationToken: token));
                Assert.AreEqual(commandId, (await actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token)).ResolutionId);
                Assert.AreEqual(selectedRevision, await supervisor.StatAsync(new(instance.InstanceId, "original.bin", root.UniquenessKey), token));
                Assert.AreEqual(0, provider.Reads);
                TestContext.WriteLine("SignedPostMove " + JsonSerializer.Serialize(new
                {
                    packageSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package!, token))),
                    originalOperationIds = plan.Members.Select(member => member.OperationId).ToArray(),
                    commandId,
                    acceptedMoveRevisionSha256 = HashRevision(moved.AcceptedRevision!),
                    selectedRevisionSha256 = HashRevision(selectedRevision),
                    acknowledgedOriginalCount = ack.Result.VerifiedMissingOriginals.Count,
                    actualSignedWorker = true,
                    fixtureOwnedOriginalEffects = true,
                    defaultHostProcessDispatchedOriginalEffects = false
                }));

                async Task<MirrorPulseJournalUploadBatch> ReadWindowAsync(int count)
                {
                    while (true)
                    {
                        var current = await journal.ReadPendingAsync(token); Assert.IsFalse(current.RequiresFullRescan);
                        if (current.ReadyCommands.Count(command => command.ItemId == itemId) >= count) return current;
                        await Task.Delay(25, token);
                    }
                }
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(commandId, token))!.State);
            Assert.AreEqual(movedHistory, JsonSerializer.Serialize(await reopened.ReadMutationAsync(moveEffectId, token)));
            Assert.AreEqual(rejectedHistory, JsonSerializer.Serialize(await reopened.ReadMutationAsync(rejectedEffectId, token)));
            Assert.AreEqual(ackHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token)));
            Assert.AreEqual(journalHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteJournalAsync(commandId, token)));
        }
        finally
        {
            if (registered) registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static string HashRevision(string revision) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision)));
}
