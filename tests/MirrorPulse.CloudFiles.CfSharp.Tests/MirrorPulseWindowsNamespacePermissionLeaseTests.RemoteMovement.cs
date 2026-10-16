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
    public Task NativeRemoteFileMovementSettlesTheOriginalBatchAcrossStoreOwnersWithoutRedispatch() =>
        RunOriginalRemoteMovementAsync(directoryMove: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteDirectoryMovementSettlesTheOriginalSubtreeAcrossStoreOwnersWithoutRedispatch() =>
        RunOriginalRemoteMovementAsync(directoryMove: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteCaseOnlyFileMovementRequiresItsActualTargetSpellingAcrossStoreOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true);

    private async Task RunOriginalRemoteMovementAsync(bool directoryMove, bool caseOnly = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.remote-movement"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        MirrorPulseNamespaceMemberMoveIntent intent = null!; MirrorPulseNamespaceMemberRegistration firstMember = null!;
        MirrorPulseNamespaceMemberMoveObservation firstNative = null!; MirrorPulseNamespaceMemberMoveProjection firstProjection = null!;
        MirrorPulseNamespaceDirectoryMoveSnapshot? firstTree = null;
        Guid operation = Guid.NewGuid(); string sourcePath = directoryMove ? "Docs/left" : "Docs/remote.bin";
        string targetPath = directoryMove ? "Docs/right/moved" : caseOnly ? "Docs/REMOTE.bin" : "Docs/right/remote.bin";
        string coldSource = directoryMove ? "Docs/left/cold.bin" : sourcePath;
        string coldTarget = directoryMove ? "Docs/right/moved/cold.bin" : targetPath;
        var sourceIdentity = router.CreateFileIdentity(registration.InstanceId, "docs", directoryMove ? "source:directory" : "source:file", "v1");
        var batch = new CloudRemoteChangeBatch($"{registration.InstanceId}/move", ReadOnlyMemory<byte>.Empty,
            [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, sourceIdentity.RemoteId, "v2",
                directoryMove ? CloudItemKind.Directory : CloudItemKind.File, targetPath, sourceIdentity.ItemId, "v1", sourcePath)], new byte[] { 1 });
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                var leftIdentity = directoryMove ? sourceIdentity : router.CreateFileIdentity(registration.InstanceId, "docs", "source:left", "v1");
                (await setup.GetDirectory("Docs").CreatePlaceholdersAsync([
                    CloudDirectoryPlaceholderSpec.CreateBuilder("left", leftIdentity).WithInSyncState(true).WithPopulationState(CloudDirectoryPopulationState.Complete).Build(),
                    CloudDirectoryPlaceholderSpec.CreateBuilder("right", router.CreateFileIdentity(registration.InstanceId, "docs", "source:right", "v1"))
                        .WithInSyncState(true).WithPopulationState(CloudDirectoryPopulationState.Complete).Build()], cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                var coldIdentity = directoryMove ? router.CreateFileIdentity(registration.InstanceId, "docs", "source:cold", "v1") : sourceIdentity;
                (await setup.GetDirectory(directoryMove ? "Docs/left" : "Docs").CreatePlaceholdersAsync([
                    CloudFilePlaceholderSpec.CreateBuilder(directoryMove ? "cold.bin" : "remote.bin", coldIdentity, 4)
                        .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                foreach (var item in new CloudItem[] { setup.Root, setup.GetDirectory("Docs"), setup.GetDirectory("Docs/left"), setup.GetDirectory("Docs/right"), setup.GetFile(coldSource) })
                    SetProtectedFixtureUserOwner(item, caller.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed(); await feed.StartAsync(deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem,
                    catalog, router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, execution, actor);
                if (owner == 0)
                {
                    var members = await catalog.ReadNamespaceMemberTreeAsync(initialized!.Completion.AnchorMemberId, deadline.Token);
                    var scan = await feed.BeginScanAsync(deadline.Token);
                    var page = await feed.ReadPageAsync(scan, 0, 64, deadline.Token);
                    Assert.IsFalse(scan.RequiresFullRescan); Assert.IsFalse(page.RequiresFullRescan);
                    if (page.Changes.Count != 0)
                    {
                        // Use the production content-only policy to settle excluded metadata
                        // before installing this move's fence. Content intent is never removed here.
                        var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => false);
                        var observed = await source.ReadPendingAsync(deadline.Token);
                        Assert.IsFalse(observed.RequiresFullRescan); Assert.IsEmpty(observed.ReadyCommands);
                    }
                    Guid memberId = members.Single(member => member.RelativePath == sourcePath).MemberId;
                    Guid targetParent = members.Single(member => member.RelativePath == (caseOnly ? "Docs" : "Docs/right")).MemberId;
                    firstMember = (await catalog.ReadNamespaceMemberAsync(memberId, deadline.Token))!;
                    intent = await coordinator.PrepareRemoteAsync(operation, memberId, targetParent,
                        directoryMove ? "moved" : caseOnly ? "REMOTE.bin" : "remote.bin", registration.InstanceId, batch, 0, deadline.Token);
                    if (directoryMove) firstTree = await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(operation, deadline.Token);
                    await coordinator.BeginRemoteAsync(operation, registration.InstanceId, batch, 0, deadline.Token);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.BeginRemoteAsync(operation,
                        registration.InstanceId, batch, 0, deadline.Token).AsTask());
                    await using (var before = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        var pending = await before.Operations.ListByItemIdAsync(sourceIdentity.ItemId, 16, deadline.Token);
                        TestContext.WriteLine($"OriginalRemoteMoveBeforeApply: directory={directoryMove}; journalKinds={string.Join(',', pending.Select(entry => entry.Kind))}.");
                        await before.RollbackAsync(deadline.Token);
                    }
                    CloudRemoteApplyResult applied = null!;
                    await actor.RunAsync(async token =>
                    {
                        await execution.RunNamespaceOperationAsync(async () =>
                            applied = await fileSystem.ApplyRemoteChangesAsync(batch, cancellationToken: token));
                        return true;
                    }, deadline.Token);
                    TestContext.WriteLine($"OriginalRemoteMoveApply: directory={directoryMove}; status={applied.Status}; entry={applied.Entries[0].Status}; retry={applied.RequiresRetry}; conflicts={applied.ConflictIds.Count}.");
                    Assert.AreEqual(CloudRemoteApplyEntryStatus.Applied, applied.Entries[0].Status);
                    Assert.IsFalse(applied.RequiresRetry); Assert.IsEmpty(applied.ConflictIds);
                    var interrupted = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, execution, actor,
                        (checkpoint, _) => checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection ?
                            ValueTask.FromException(new IOException("Synthetic stop after remote official projection.")) : ValueTask.CompletedTask);
                    await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.SettleRemoteAsync(operation,
                        registration.InstanceId, batch, 0, deadline.Token).AsTask());
                    firstNative = (await catalog.ReadNamespaceMemberMoveObservationAsync(operation, deadline.Token))!;
                    firstProjection = (await catalog.ReadNamespaceMemberMoveProjectionAsync(operation, deadline.Token))!;
                    Assert.IsNotNull(firstNative); Assert.IsNotNull(firstProjection);
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                    Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(memberId, deadline.Token)).RelativePath);
                }
                else
                {
                    CloudRemoteApplyResult replay = null!;
                    await execution.RunNamespaceOperationAsync(async () => replay = await fileSystem.ApplyRemoteChangesAsync(batch, cancellationToken: deadline.Token));
                    Assert.AreEqual(CloudRemoteApplyEntryStatus.AlreadyApplied, replay.Entries[0].Status);
                    var completed = await coordinator.SettleRemoteAsync(operation, registration.InstanceId, batch, 0, deadline.Token);
                    Assert.AreEqual(completed, await coordinator.SettleRemoteAsync(operation, registration.InstanceId, batch, 0, deadline.Token));
                    Assert.AreEqual(firstMember, await catalog.ReadNamespaceMemberAsync(intent.MemberId, deadline.Token));
                    Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(operation, deadline.Token));
                    Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(operation, deadline.Token));
                    Assert.AreEqual(targetPath, (await catalog.ReadNamespaceMemberLocationAsync(intent.MemberId, deadline.Token)).RelativePath);
                    Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                    if (firstTree is not null)
                        foreach (var entry in firstTree.Entries)
                        {
                            var location = await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, deadline.Token);
                            Assert.AreEqual(targetPath + entry.RelativePath[sourcePath.Length..], location.RelativePath);
                            Assert.AreEqual(entry.LocalObject, (await catalog.ReadNamespaceMemberAsync(entry.MemberId, deadline.Token))!.LocalObject);
                        }
                }
                var cold = await fileSystem.GetFile(coldTarget).InspectAsync(deadline.Token);
                Assert.AreEqual(CloudContentAvailability.OnlineOnly, cold.ContentAvailability);
                Assert.AreEqual(0, provider.Reads);
                Assert.IsNull(await new MirrorPulseRemoteBatchCoordinator(fileSystem, state).ReadCursorAsync(registration.InstanceId, deadline.Token));
                CloudItem original = directoryMove ? fileSystem.GetDirectory(sourcePath) : fileSystem.GetFile(sourcePath);
                if (caseOnly)
                {
                    var actualNames = new List<string>();
                    await foreach (var item in fileSystem.GetDirectory("Docs").EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, deadline.Token))
                        actualNames.Add(item.RelativePath.Replace('\\', '/'));
                    Assert.Contains(targetPath, actualNames); Assert.DoesNotContain(sourcePath, actualNames);
                }
                else Assert.IsFalse((await original.InspectAsync(deadline.Token)).Exists);
                TestContext.WriteLine($"OriginalRemoteMove: owner={owner}; directory={directoryMove}; caseOnly={caseOnly}; originalBatch=True; beforeStartBinding=True; " +
                    $"publicSdkExecution=True; publicDirectoryReconciliation={directoryMove}; productPositionSeparate=True; adapterCursorPublished=False; hydration=False; sameRoleAcrossSdkOwners=True; defaultHostIntegrated=False.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath); RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }

    private sealed class RemoteMovementContentGuard : ICloudFileContentProvider
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _reads); return ValueTask.FromException<Stream>(new AssertFailedException("Remote movement must not hydrate cold content."));
        }
    }
}
