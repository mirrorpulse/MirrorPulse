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
    public async Task NativeCoalescedKeepBothPreservesSelectedRemoteAcrossThreeOwnersBeforeOriginalAcknowledgement()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.keep-both"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var remote = new KeepBothRemote(Path.Combine(paths.DataRootPath, "keep-both-remote.bin"));
        var actualCopyVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid commandId = Guid.Empty, logicalItem = Guid.Empty; Guid[] originalIds = []; byte[] expectedLocal = [];
        string? preservedPath = null; int readsAfterFirstCopy = 0;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 13]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
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
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 }); await feed.StartAsync(deadline.Token);
                var center = new MirrorPulseConflictCenter();
                var actions = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
                    paths, router, remote, center, scheduler, fileSystem, actor, role, stats: remote, remoteRanges: remote, remoteDirectories: remote)
                {
                    KeepBothCheckpoint = (checkpoint, _) =>
                    {
                        if (owner == 2 && checkpoint == MirrorPulseKeepBothCheckpoint.BeforeEffectCopyVerification) actualCopyVerification.TrySetResult();
                        return owner == 0 && checkpoint == MirrorPulseKeepBothCheckpoint.AfterRemotePreserved ||
                            owner == 1 && checkpoint == MirrorPulseKeepBothCheckpoint.AfterRetryAuthorized
                                ? ValueTask.FromException(new IOException("Synthetic preservation boundary interruption.")) : ValueTask.CompletedTask;
                    }
                };
                if (owner == 0)
                {
                    var file = fileSystem.GetFile("Docs/original.bin"); logicalItem = (await file.InspectAsync(deadline.Token)).ItemId!.Value;
                    var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    await File.AppendAllTextAsync(file.FullPath, " first local edit", deadline.Token);
                    MirrorPulseJournalUploadBatch window;
                    while (!(window = await journal.ReadPendingAsync(deadline.Token)).ReadyCommands.Any(value => value.ItemId == logicalItem))
                        await Task.Delay(25, deadline.Token);
                    await File.AppendAllTextAsync(file.FullPath, " latest local edit", deadline.Token);
                    while ((window = await journal.ReadPendingAsync(deadline.Token)).ReadyCommands.Count(value => value.ItemId == logicalItem) < 2)
                        await Task.Delay(25, deadline.Token);
                    expectedLocal = await File.ReadAllBytesAsync(file.FullPath, deadline.Token);
                    var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role)
                        .CaptureUploadBindingAsync(file, await file.InspectAsync(deadline.Token), deadline.Token);
                    var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, logicalItem, window.WindowUpperSequence, new HashSet<Guid>())!;
                    Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile, plan.Effect);
                    originalIds = plan.Members.Select(value => value.OperationId).ToArray();
                    await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, "prior-remote",
                        new(expectedLocal.Length, Convert.ToHexString(SHA256.HashData(expectedLocal)), binding), deadline.Token);
                    remote.Seed();
                    await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() =>
                        new MirrorPulseJournalCoalescingExecutor(catalog, remote, remote, remote, new(remote)).ExecuteAsync(plan.PlanId,
                            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(expectedLocal)), deadline.Token).AsTask());
                    Guid conflictId = Guid.Empty;
                    foreach (Guid id in originalIds)
                    {
                        var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), root.InstanceId, id.ToString("D"), "Docs/original.bin",
                            MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "prior-remote", "selected-remote", DateTimeOffset.UtcNow);
                        await catalog.SaveUploadConflictAsync(conflict, deadline.Token); center.Upsert(conflict); conflictId = conflict.ConflictId;
                    }
                    await Assert.ThrowsExactlyAsync<IOException>(() => actions.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepBoth, deadline.Token).AsTask());
                    commandId = (await catalog.ReadConflictKeepBothOwnerAsync(originalIds[0], deadline.Token))!.Value;
                    var prepared = (await catalog.ReadConflictKeepBothAsync(commandId, deadline.Token))!;
                    Assert.IsNull(prepared.Copy); Assert.IsFalse(prepared.RetryAuthorized);
                    Assert.HasCount(originalIds.Length, await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token));
                    readsAfterFirstCopy = remote.SourceReads; Assert.IsGreaterThan(0, readsAfterFirstCopy);
                }
                else if (owner == 1)
                {
                    remote.DenySourceReads = true;
                    await Assert.ThrowsExactlyAsync<IOException>(() => actions.ResumeConflictKeepBothAsync(commandId, deadline.Token).AsTask());
                    var retained = (await catalog.ReadConflictKeepBothAsync(commandId, deadline.Token))!;
                    Assert.IsNotNull(retained.Copy); Assert.IsTrue(retained.RetryAuthorized); Assert.IsFalse(retained.OriginalRetriesPrepared);
                    Assert.AreEqual(readsAfterFirstCopy, remote.SourceReads, "A committed original copy must recover without Worker metadata or content reads.");
                    Assert.IsEmpty(await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token));
                    await using var pending = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (Guid id in originalIds) Assert.IsNotNull(await pending.Operations.GetAsync(id, deadline.Token));
                    await pending.RollbackAsync(deadline.Token);
                }
                else
                {
                    remote.DenySourceReads = false;
                    var resumed = await actions.ResumeConflictKeepBothAsync(commandId, deadline.Token);
                    Assert.IsTrue((await catalog.ReadConflictKeepBothAsync(commandId, deadline.Token))!.OriginalRetriesPrepared);
                    Assert.IsNotNull(resumed.PreservedPath);
                    byte[] archive = await File.ReadAllBytesAsync(resumed.PreservedPath, deadline.Token);
                    await File.WriteAllTextAsync(resumed.PreservedPath, "changed archive before actual dispatch", deadline.Token);
                    await using var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, remote, remote, state, paths.SyncRootPath,
                        paths.DataRootPath, _ => true, new(feed, state, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))), conflicts: center,
                        mutations: remote, ranges: remote, directories: remote, fileSystem: fileSystem, scheduler: scheduler,
                        namespaceActor: actor, namespaceExecution: role, conflictActions: actions);
                    await pump.StartAsync(deadline.Token);
                    await actualCopyVerification.Task.WaitAsync(deadline.Token);
                    while (pump.Health.Healthy) await Task.Delay(25, deadline.Token);
                    Assert.AreEqual(0, remote.AcceptedUploads, "A prior copy receipt cannot authorize a write after its actual archive changed.");
                    Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId, deadline.Token))!.State);
                    CollectionAssert.AreEqual(KeepBothRemote.SelectedBytes, await File.ReadAllBytesAsync(remote.Path, deadline.Token));
                    await File.WriteAllBytesAsync(resumed.PreservedPath, archive, deadline.Token);
                    while ((await catalog.ReadUserCommandAsync(commandId, deadline.Token))!.State != "resolved") await Task.Delay(25, deadline.Token);
                    var complete = (await catalog.ReadConflictKeepBothAsync(commandId, deadline.Token))!;
                    Assert.IsTrue(complete.RetryAuthorized); Assert.IsTrue(complete.OriginalRetriesPrepared);
                    var selected = (await catalog.ReadUploadConflictAsync(complete.Intent.ConflictId, deadline.Token))!;
                    await using var copy = await new MirrorPulseConflictCopyStore(paths).OpenRemoteDecisionAsync(commandId, selected, complete.Intent.RemoteSource, deadline.Token);
                    preservedPath = copy.Path;
                    CollectionAssert.AreEqual(KeepBothRemote.SelectedBytes, await File.ReadAllBytesAsync(copy.Path, deadline.Token));
                    CollectionAssert.AreEqual(expectedLocal, await File.ReadAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, deadline.Token));
                    CollectionAssert.AreEqual(expectedLocal, await File.ReadAllBytesAsync(remote.Path, deadline.Token));
                    Assert.AreEqual(1, remote.AcceptedUploads); Assert.IsFalse(originalIds.Contains(remote.AcceptedOperation));
                    Assert.AreEqual(logicalItem, (await fileSystem.GetFile("Docs/original.bin").InspectAsync(deadline.Token)).ItemId);
                    await using var pending = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (Guid id in originalIds) Assert.IsNull(await pending.Operations.GetAsync(id, deadline.Token));
                    await pending.RollbackAsync(deadline.Token);
                }
            }
            Assert.IsNotNull(preservedPath); Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine($"KeepBoth: productionAction=True; productionRecoveryPump=True; threeSdkOwners=True; sameRole=True; originalCount={originalIds.Length}; selectedRemoteCopyVerified=True; committedCopyReopenWithoutWorkerReads=True; changedArchiveBlocksActualRemoteWrite=True; latestLocalContentPreserved=True; distinctConditionalUpload=True; actualNativeConfirmation=True; originalAcknowledgementAfterAcceptance=True; sourceHydrationReads=0; defaultHostIntegration=False; remoteTransportFixture=True.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private sealed class KeepBothRemote(string path) : IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerRangeTransport,
        IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerMutationTransport
    {
        internal static readonly byte[] SelectedBytes = [6, 6, 6];
        private string _revision = "selected-remote";
        internal string Path => path;
        internal bool DenySourceReads { get; set; }
        internal int SourceReads { get; private set; }
        internal int AcceptedUploads { get; private set; }
        internal Guid AcceptedOperation { get; private set; }
        internal void Seed() { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllBytes(path, SelectedBytes); }
        private void Read(CancellationToken token) { token.ThrowIfCancellationRequested(); Assert.IsFalse(DenySourceReads, "Recovery cannot reopen a committed remote source."); SourceReads++; }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { Read(token); Assert.AreEqual("original.bin", request.NormalizedPath); return ValueTask.FromResult<string?>(_revision); }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            Read(token); Assert.AreEqual(string.Empty, request.NormalizedPath);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new("remote-object", _revision, "File", "original.bin", new FileInfo(path).Length, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        { Read(token); return ValueTask.FromResult<Stream>(new MemoryStream(File.ReadAllBytes(path), checked((int)request.Offset), checked((int)request.Length), writable: false)); }
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.ExpectedRevision != _revision) throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, _revision);
            await using var data = new MemoryStream(); await request.Content.CopyToAsync(data, token);
            byte[] bytes = data.ToArray(); Assert.AreEqual(request.Length, bytes.LongLength); Assert.AreEqual(request.ExpectedContentSha256, Convert.ToHexString(SHA256.HashData(bytes)));
            await File.WriteAllBytesAsync(path, bytes, token); AcceptedUploads++; AcceptedOperation = request.OperationId!.Value;
            return _revision = "accepted-local";
        }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) => throw new AssertFailedException("This fixture admits no move.");
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token) => throw new AssertFailedException("KeepBoth cannot delete either side.");
    }
}
