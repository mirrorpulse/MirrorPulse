using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeGroupRemoteDeletionPreservesLocalBytesAndRecoversWithoutReupload() => RunConflictDeletionAsync(grouped: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeSingleRemoteDeletionPreservesLocalBytesAndRecoversWithoutReupload() => RunConflictDeletionAsync(grouped: false);

    private async Task RunConflictDeletionAsync(bool grouped)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.conflict-deletion"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var remote = new ConflictDeletionRemote(Path.Combine(paths.DataRootPath, "synthetic-remote.bin"));
        Guid commandId = Guid.Empty; Guid[] originalIds = []; Guid conflictId = Guid.Empty;
        byte[] expected = []; Guid logicalItemId = Guid.Empty; string? preservedPath = null; int rejectedUploadCalls = 0;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 12]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1, 2, 3], deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                }
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(fileSystem.Root, router, deadline.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                await feed.StartAsync(deadline.Token);
                var center = new MirrorPulseConflictCenter();
                var actions = new MirrorPulseUploadConflictActions(catalog, state, feed,
                    new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), paths, router, remote, center,
                    scheduler, fileSystem, actor, role, stats: remote);
                if (owner == 0)
                {
                    var file = fileSystem.GetFile("Docs/original.bin");
                    logicalItemId = (await file.InspectAsync(deadline.Token)).ItemId!.Value;
                    var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    await File.AppendAllTextAsync(file.FullPath, " first local edit", deadline.Token);
                    MirrorPulseJournalUploadBatch window;
                    while (!(window = await journal.ReadPendingAsync(deadline.Token)).ReadyCommands.Any(value => value.ItemId == logicalItemId))
                        await Task.Delay(25, deadline.Token);
                    if (grouped)
                    {
                        await File.AppendAllTextAsync(file.FullPath, " latest local edit", deadline.Token);
                        while ((window = await journal.ReadPendingAsync(deadline.Token)).ReadyCommands.Count(value => value.ItemId == logicalItemId) < 2)
                            await Task.Delay(25, deadline.Token);
                    }
                    expected = await File.ReadAllBytesAsync(file.FullPath, deadline.Token);
                    var capture = new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role);
                    var binding = await capture.CaptureUploadBindingAsync(file, await file.InspectAsync(deadline.Token), deadline.Token);
                    var content = new MirrorPulseCoalescingContent(expected.Length, Convert.ToHexString(SHA256.HashData(expected)), binding);
                    // The prior remote revision is a fixture premise. Every original
                    // journal row, rejection, archive, local retirement and ACK is real.
                    const string baseline = "prior-remote";
                    remote.Seed();
                    if (grouped)
                    {
                        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, logicalItemId, window.WindowUpperSequence, new HashSet<Guid>())!;
                        Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile, plan.Effect);
                        originalIds = plan.Members.Select(value => value.OperationId).ToArray();
                        await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, baseline, content, deadline.Token);
                        var effects = new MirrorPulseJournalCoalescingExecutor(catalog, remote, remote, remote, new(remote));
                        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => effects.ExecuteAsync(plan.PlanId,
                            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(expected)), deadline.Token).AsTask());
                    }
                    else
                    {
                        var original = window.ReadyCommands.First(value => value.ItemId == logicalItemId);
                        originalIds = [original.OperationId];
                        var preflightFeed = feed; // The SDK owns one already-started feed, shared with conflict actions.
                        Assert.IsTrue(preflightFeed.IsStarted);
                        var completion = new MirrorPulseJournalUploadCompletion(preflightFeed, state,
                            new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
                        // A content-only Worker disables coalescing here, exercising
                        // the production revision guard before any mutation intent.
                        await using var pump = new MirrorPulseJournalUploadPump(preflightFeed, router, catalog, remote, remote, state,
                            paths.SyncRootPath, paths.DataRootPath, _ => true, completion, conflicts: center,
                            fileSystem: fileSystem, scheduler: scheduler, namespaceActor: actor, namespaceExecution: role);
                        await pump.StartAsync(deadline.Token);
                        MirrorPulseConflictRecord? preflight;
                        while ((preflight = (await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token))
                            .SingleOrDefault(value => value.ChangeId == original.OperationId.ToString("D"))) is null)
                            await Task.Delay(25, deadline.Token);
                        conflictId = preflight.ConflictId;
                        Assert.IsNull(await catalog.ReadMutationAsync(original.OperationId, deadline.Token));
                    }
                    rejectedUploadCalls = remote.UploadCalls;
                    Assert.IsTrue(feed.IsStarted, "Stopping the pump must retain the SDK-owned feed for conflict actions.");
                    foreach (Guid original in grouped ? originalIds : [])
                    {
                        var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), root.InstanceId, original.ToString("D"), "Docs/original.bin",
                            MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, baseline, "concurrent-remote", DateTimeOffset.UtcNow);
                        await catalog.SaveUploadConflictAsync(conflict, deadline.Token); center.Upsert(conflict); conflictId = conflict.ConflictId;
                    }
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(file.FullPath), deadline.Token));
                    await Assert.ThrowsExactlyAsync<IOException>(() => actions.ApplyAsync(conflictId, MirrorPulseConflictAction.DeleteRemote, deadline.Token).AsTask());
                    commandId = (await catalog.ReadConflictDeletionOwnerAsync(originalIds[0], deadline.Token))!.Value;
                    var deletion = (await catalog.ReadConflictDeletionAsync(commandId, deadline.Token))!;
                    Assert.AreEqual(grouped ? 1 : 2, deletion.Intent.Version);
                    if (!grouped)
                    {
                        Assert.IsNotNull(deletion.Intent.OriginalObservation);
                        Assert.IsNull(await catalog.ReadMutationAsync(originalIds[0], deadline.Token));
                    }
                    Assert.AreEqual(MirrorPulseConflictDeletionPhase.Retired, deletion.Phase);
                    Assert.AreEqual(MirrorPulseMutationState.Ambiguous, (await catalog.ReadMutationAsync(deletion.Intent.RemoteDeletion.OperationId, deadline.Token))!.State);
                    Assert.AreEqual(1, remote.DeleteCalls); Assert.IsFalse(File.Exists(remote.Path));
                    await using var pending = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (Guid id in originalIds) Assert.IsNotNull(await pending.Operations.GetAsync(id, deadline.Token));
                    await pending.RollbackAsync(deadline.Token);
                }
                else
                {
                    foreach (var conflict in await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token)) center.Upsert(conflict);
                    var result = await actions.ResumeConflictDeletionAsync(commandId, deadline.Token);
                    preservedPath = result.PreservedPath;
                    Assert.IsNotNull(preservedPath); CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(preservedPath, deadline.Token));
                    Assert.AreEqual(MirrorPulseConflictDeletionPhase.Completed, (await catalog.ReadConflictDeletionAsync(commandId, deadline.Token))!.Phase);
                    Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId, deadline.Token))!.State);
                    Assert.IsEmpty(center.Query()); Assert.IsEmpty(await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token));
                    await using var pending = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (Guid id in originalIds) Assert.IsNull(await pending.Operations.GetAsync(id, deadline.Token));
                    await pending.RollbackAsync(deadline.Token);
                }
                var tombstone = await fileSystem.GetFile("Docs/original.bin").InspectAsync(deadline.Token);
                Assert.IsFalse(tombstone.Exists); Assert.IsTrue(tombstone.IsTombstone); Assert.AreEqual(logicalItemId, tombstone.ItemId);
                Assert.AreEqual(1, remote.DeleteCalls); Assert.AreEqual(rejectedUploadCalls, remote.UploadCalls);
            }
            Assert.IsNotNull(preservedPath); Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine($"ConflictDeletion: grouped={grouped}; productionAction=True; protectedRetirement=True; latestLocalBytesArchived=True; twoSdkOwners=True; lostReplyReadback=True; originalCount={originalIds.Length}; remoteDeleteCalls=1; postDecisionUploadAttempts={remote.UploadCalls - rejectedUploadCalls}; sourceReads=0; defaultHostIntegration=False; remoteTransportFixture=True.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private sealed class ConflictDeletionRemote(string path) : IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerUploadTransport
    {
        internal string Path => path;
        internal int DeleteCalls { get; private set; }
        internal int UploadCalls { get; private set; }
        internal void Seed() => File.WriteAllBytes(path, [6, 6, 6]);
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(File.Exists(path) ? "concurrent-remote" : null); }
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        { UploadCalls++; throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, "concurrent-remote"); }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) => throw new AssertFailedException("No remote move is authorized.");
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual("original.bin", request.NormalizedPath);
            Assert.AreEqual("docs", request.RootKey); Assert.AreEqual("concurrent-remote", request.ExpectedRevision);
            Assert.IsFalse(request.IsDirectory); Assert.IsNotNull(request.OperationId);
            if (!File.Exists(path)) throw new AssertFailedException("Lost replies must reconcile rather than delete again.");
            File.Delete(path); DeleteCalls++;
            throw new IOException("Injected lost reply after the conditional remote delete.");
        }
    }
}
