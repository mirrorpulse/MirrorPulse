using System.ComponentModel;
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

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteFileMovementRetainsTheOriginalWhenPermissionNotificationsArePending() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, retainPending: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteCaseOnlyMovementRetainsTheOriginalWhenPermissionNotificationsArePending() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true, retainPending: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteFileConflictRejectionReleasesOnlyItsOriginalAdmissionAcrossStoreOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, retainPending: true, settleRejected: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeRemoteCaseOnlyConflictRejectionReleasesOnlyItsOriginalAdmissionAcrossStoreOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true, retainPending: true, settleRejected: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteFileBatchRecoversSdkCommitBeforeMemberSettlementAndCursor() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, managedBatch: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteStaleFileBatchRejectsAgainstOriginalOfficialRevisionAcrossOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, managedBatch: true, staleExpectedRevision: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteStaleCaseOnlyBatchRejectsAgainstOriginalOfficialRevisionAcrossOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true, managedBatch: true, staleExpectedRevision: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteFileBatchRecoversAnUnmovedOriginalStartAcrossOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, managedBatch: true, stopBeforeSdk: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeManagedRemoteCaseOnlyBatchRecoversOnlyItsActualUnmovedSourceAcrossOwners() =>
        RunOriginalRemoteMovementAsync(directoryMove: false, caseOnly: true, managedBatch: true, stopBeforeSdk: true);

    private async Task RunOriginalRemoteMovementAsync(bool directoryMove, bool caseOnly = false, bool retainPending = false,
        bool settleRejected = false, bool managedBatch = false, bool staleExpectedRevision = false, bool stopBeforeSdk = false,
        bool failProgressCommit = false)
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
        MirrorPulseNamespaceRemoteMoveRejection? firstRejection = null;
        MirrorPulseNamespaceMemberMoveStart? firstStart = null;
        MirrorPulseNamespaceMemberMoveIntent? peer = null;
        Guid[] originalJournal = [];
        Guid operation = Guid.NewGuid(); string sourcePath = directoryMove ? "Docs/left" : "Docs/remote.bin";
        string targetPath = directoryMove ? "Docs/right/moved" : caseOnly ? "Docs/REMOTE.bin" : "Docs/right/remote.bin";
        string coldSource = directoryMove ? "Docs/left/cold.bin" : sourcePath;
        string coldTarget = directoryMove ? "Docs/right/moved/cold.bin" : targetPath;
        var sourceIdentity = router.CreateFileIdentity(registration.InstanceId, "docs", directoryMove ? "source:directory" : "source:file", "v1");
        var batch = new CloudRemoteChangeBatch($"{registration.InstanceId}/move", ReadOnlyMemory<byte>.Empty,
            [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, sourceIdentity.RemoteId, "v2",
                directoryMove ? CloudItemKind.Directory : CloudItemKind.File, targetPath, sourceIdentity.ItemId,
                staleExpectedRevision ? "stale-expected-revision" : "v1", sourcePath)], new byte[] { 1 });
        var progressFault = new MirrorPulseRemoteProgressCommitFault(batch);
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
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(failProgressCommit && owner == 0 ? progressFault.Wrap(state) : state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                // Separate the clean remote-move boundary from the live watcher's conservative
                // content notifications during ACL initialization. Both paths are mandatory.
                if (directoryMove || retainPending || owner != 0) await feed.StartAsync(deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem,
                    catalog, router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                if (!directoryMove && !retainPending && owner == 0) await feed.StartAsync(deadline.Token);
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, execution, actor);
                if (owner == 0)
                {
                    var members = await catalog.ReadNamespaceMemberTreeAsync(initialized!.Completion.AnchorMemberId, deadline.Token);
                    if (retainPending)
                    {
                        // A metadata-only change is deliberately observed by the real watcher.
                        // CfSharp conservatively classifies file notifications as content intent.
                        await execution.RunNamespaceOperationAsync(() =>
                        {
                            File.SetLastWriteTimeUtc(fileSystem.GetFile(sourcePath).FullPath, DateTime.UtcNow.AddSeconds(-3));
                            return Task.CompletedTask;
                        });
                        _ = await feed.ReadBatchAsync(deadline.Token);
                        // Wait for the exact public journal row, not an earlier directory
                        // notification or an arbitrary delay after FileSystemWatcher delivery.
                        while (true)
                        {
                            await using var observation = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                            var pendingContent = await observation.Operations.ListByItemIdAsync(sourceIdentity.ItemId, 16, deadline.Token);
                            await observation.RollbackAsync(deadline.Token);
                            if (pendingContent.Any(entry => entry.Kind == CloudStateOperationKind.ContentUpdate)) break;
                            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
                        }
                    }
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
                    if (managedBatch)
                    {
                        var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                            (checkpoint, _) => !failProgressCommit && checkpoint == (stopBeforeSdk ? MirrorPulseRemoteNamespaceBatchCheckpoint.AfterMoveStart :
                                MirrorPulseRemoteNamespaceBatchCheckpoint.AfterSdkApply)
                                ? ValueTask.FromException(new IOException("Synthetic stop at the original remote dispatch boundary.")) : ValueTask.CompletedTask);
                        var engine = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                        await Assert.ThrowsExactlyAsync<IOException>(() => engine.ApplyAsync(registration.InstanceId, batch,
                            cancellationToken: deadline.Token).AsTask());
                        intent = (await catalog.ReadNamespaceRemoteMoveAsync(registration.InstanceId, batch.BatchId, 0, deadline.Token))!;
                        Assert.IsNotNull(intent); operation = intent.OperationId;
                        var originalStart = await catalog.ReadNamespaceMemberMoveStartAsync(operation, deadline.Token);
                        Assert.IsNotNull(originalStart); Assert.AreEqual(2, originalStart.Version);
                        Assert.AreEqual("v1", originalStart.OriginalOfficialRemoteRevision);
                        firstStart = originalStart;
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                        Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(memberId, deadline.Token)).RelativePath);
                        Assert.IsNull(await engine.ReadCursorAsync(registration.InstanceId, deadline.Token));
                        if (failProgressCommit)
                        {
                            Assert.AreEqual(1, progressFault.Failures); Assert.IsNotNull(progressFault.UncommittedProgress);
                            Assert.AreEqual(1, progressFault.UncommittedProgress.AppliedEntryCount);
                            await VerifyUncommittedRemoteMoveAsync(fileSystem, state, catalog, intent, firstStart, batch, sourcePath, targetPath, deadline.Token);
                            Assert.AreEqual(0, provider.Reads);
                            continue;
                        }
                        await using var committed = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        var progress = await committed.RemoteBatches.GetAsync(batch.BatchId, deadline.Token);
                        if (stopBeforeSdk)
                        {
                            Assert.IsNull(progress);
                            await committed.RollbackAsync(deadline.Token);
                            var retainedSource = await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token);
                            Assert.AreEqual(firstMember.LocalObject.LocalFileId, retainedSource.LocalBinding!.LocalFileId);
                            Assert.AreEqual(CloudContentAvailability.OnlineOnly, retainedSource.ContentAvailability);
                            Assert.AreEqual(0, provider.Reads);
                            continue;
                        }
                        Assert.IsNotNull(progress); Assert.AreEqual(1, progress.AppliedEntryCount);
                        Assert.AreEqual(CloudRemoteBatchStatus.Applied, progress.Status);
                        await committed.RollbackAsync(deadline.Token);
                        if (staleExpectedRevision)
                        {
                            var conflicts = await fileSystem.ListRemoteConflictsAsync(deadline.Token);
                            var conflict = conflicts.Single(record => record.Conflict.Change.ChangeId == "move");
                            Assert.AreEqual(CloudRemoteConflictReason.StaleRemoteRevision, conflict.Conflict.Reason);
                            Assert.AreEqual("stale-expected-revision", conflict.Conflict.Change.PreviousRemoteRevision);
                            Assert.AreEqual(firstMember.LocalObject.LocalFileId, (await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                        }
                        continue;
                    }
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
                    if (retainPending)
                    {
                        Assert.AreEqual(CloudRemoteApplyEntryStatus.Conflict, applied.Entries[0].Status);
                        Assert.IsFalse(applied.RequiresRetry); Assert.HasCount(1, applied.ConflictIds);
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(operation, deadline.Token));
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(operation, deadline.Token));
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                        Assert.AreEqual(operation, (await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token))!.OperationId);
                        Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(memberId, deadline.Token)).RelativePath);
                        var unchanged = await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token);
                        Assert.IsTrue(unchanged.Exists); Assert.AreEqual(firstMember.LocalObject.LocalFileId, unchanged.LocalBinding!.LocalFileId);
                        Assert.AreEqual(CloudContentAvailability.OnlineOnly, unchanged.ContentAvailability);
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.SettleRemoteAsync(operation,
                            registration.InstanceId, batch, 0, deadline.Token).AsTask());
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.BeginRemoteAsync(operation,
                            registration.InstanceId, batch, 0, deadline.Token).AsTask());
                        await using var retained = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        Assert.IsTrue((await retained.Operations.ListByItemIdAsync(sourceIdentity.ItemId, 16, deadline.Token))
                            .Any(entry => entry.Kind == CloudStateOperationKind.ContentUpdate));
                        await retained.RollbackAsync(deadline.Token);
                        Assert.AreEqual(0, provider.Reads);
                        Assert.IsNull(await new MirrorPulseRemoteBatchCoordinator(fileSystem, state).ReadCursorAsync(registration.InstanceId, deadline.Token));
                        TestContext.WriteLine($"OriginalRemoteMoveConflict: caseOnly={caseOnly}; liveFeedDuringProtection=True; defaultSdkOptions=True; " +
                            "originalObjectAndJournalRetained=True; noNativeMove=True; noFalseSettlement=True; redispatchRefused=True; " +
                            "pendingFenceRetained=True; adapterCursorPublished=False; hydration=False; defaultHostIntegrated=False.");
                        if (!settleRejected) return;
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.RejectRemoteAsync(operation,
                            registration.InstanceId, batch, 0, Guid.NewGuid(), deadline.Token).AsTask());
                        await using (var originalRows = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                        {
                            var rows = await originalRows.Operations.ListByItemIdAsync(sourceIdentity.ItemId, 64, deadline.Token);
                            Assert.IsLessThan(64, rows.Count); originalJournal = rows.Select(row => row.OperationId).ToArray();
                            Assert.IsNotEmpty(originalJournal); await originalRows.RollbackAsync(deadline.Token);
                        }
                        firstRejection = await coordinator.RejectRemoteAsync(operation, registration.InstanceId, batch, 0,
                            applied.ConflictIds.Single(), deadline.Token);
                        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                        Assert.AreEqual("Rejected", (await catalog.ReadNamespaceMemberMoveStatusAsync(operation, deadline.Token))!.Phase);
                        peer = await coordinator.PrepareAsync(Guid.NewGuid(), memberId, targetParent,
                            caseOnly ? "REMOTE.bin" : "remote.bin", deadline.Token);
                        Assert.AreEqual(firstRejection, await coordinator.RejectRemoteAsync(operation, registration.InstanceId, batch, 0,
                            firstRejection.ConflictId, deadline.Token));
                        Assert.AreEqual(peer, await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                        continue;
                    }
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
                else if (managedBatch)
                {
                    var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor);
                    var engine = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                    if (failProgressCommit)
                    {
                        await VerifyUncommittedRemoteMoveAsync(fileSystem, state, catalog, intent, firstStart!, batch, sourcePath, targetPath, deadline.Token);
                        Exception? blocked = null;
                        try { await engine.ApplyAsync(registration.InstanceId, batch, cancellationToken: deadline.Token); }
                        catch (Exception error) when (error is IOException or InvalidDataException) { blocked = error; }
                        catch (Win32Exception error) when (error.NativeErrorCode == 2) { blocked = error; }
                        Assert.IsNotNull(blocked, "An unknown native result must not be redispatched or settled from incomplete SDK progress.");
                        TestContext.WriteLine($"OriginalRemoteProgressGuard: caseOnly={caseOnly}; failureType={blocked.GetType().Name}; sdkProgressStillUncommitted=True.");
                        await VerifyUncommittedRemoteMoveAsync(fileSystem, state, catalog, intent, firstStart!, batch, sourcePath, targetPath, deadline.Token);
                        CloudRemoteApplyResult actualReplay = null!;
                        // Exercise the library's original public replay independently of the
                        // product guard. Keep its actual result and durable conflict as evidence.
                        await actor.RunAsync(async token =>
                        {
                            await execution.RunNamespaceOperationAsync(async () =>
                                actualReplay = await fileSystem.ApplyRemoteChangesAsync(batch, cancellationToken: token));
                            return true;
                        }, deadline.Token);
                        TestContext.WriteLine($"OriginalRemoteProgressSdkReplay: status={actualReplay.Status}; entries={string.Join(',', actualReplay.Entries.Select(entry => entry.Status))}; " +
                            $"count={actualReplay.AppliedEntryCount}; retry={actualReplay.RequiresRetry}; conflicts={actualReplay.ConflictIds.Count}.");
                        Assert.AreEqual(CloudRemoteApplyEntryStatus.Conflict, actualReplay.Entries.Single().Status);
                        Assert.AreEqual(1, actualReplay.AppliedEntryCount); Assert.IsFalse(actualReplay.RequiresRetry);
                        var record = await fileSystem.GetRemoteConflictAsync(actualReplay.ConflictIds.Single(), deadline.Token);
                        Assert.IsNotNull(record); Assert.AreEqual(CloudRemoteConflictReason.StaleRemoteRevision, record.Conflict.Reason);
                        Assert.AreEqual("v1", record.Conflict.Change.PreviousRemoteRevision);
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                        Assert.IsNull(await engine.ReadCursorAsync(registration.InstanceId, deadline.Token));
                        Assert.AreEqual(firstMember.LocalObject.LocalFileId, (await fileSystem.GetFile(targetPath).InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                        Assert.AreEqual(0, provider.Reads);
                        TestContext.WriteLine($"OriginalRemoteProgressGap: caseOnly={caseOnly}; twoSdkOwners=True; nativeMoveAndOfficialItemCommitted=True; " +
                            "originalProgressCommitLost=True; originalStartAndBindingPreserved=True; productRecoveryBlockedBeforeSdkRedispatch=True; " +
                            $"publicSdkReplayEntry={actualReplay.Entries.Single().Status}; publicSdkReplayReason={record.Conflict.Reason}; " +
                            "adapterCursorPublished=False; productMoveCompleted=False; hydration=False; syntheticPersistenceFailureHasNoNativeError=True; " +
                            "automaticOutcomeRecoveryDelivered=False; sameRoleAcrossSdkOwners=True; defaultHostIntegrated=False.");
                        continue;
                    }
                    var replay = await engine.ApplyAsync(registration.InstanceId, batch, cancellationToken: deadline.Token);
                    Assert.AreEqual(stopBeforeSdk ? CloudRemoteApplyEntryStatus.Applied : CloudRemoteApplyEntryStatus.AlreadyApplied,
                        replay.Entries.Single().Status);
                    Assert.IsFalse(replay.RequiresRetry);
                    Assert.AreEqual(intent, await catalog.ReadNamespaceRemoteMoveAsync(registration.InstanceId, batch.BatchId, 0, deadline.Token));
                    Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(operation, deadline.Token));
                    if (staleExpectedRevision)
                    {
                        var rejected = await catalog.ReadNamespaceRemoteMoveRejectionAsync(operation, deadline.Token);
                        Assert.IsNotNull(rejected); Assert.AreEqual("v1", rejected.OfficialRemoteRevision);
                        Assert.AreEqual("stale-expected-revision", intent.RemoteEntry!.PreviousRemoteRevision);
                        Assert.IsNotNull(await fileSystem.GetRemoteConflictAsync(rejected.ConflictId, deadline.Token));
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                        Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(intent.MemberId, deadline.Token)).RelativePath);
                        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                        var retainedSource = await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token);
                        Assert.AreEqual(firstMember.LocalObject.LocalFileId, retainedSource.LocalBinding!.LocalFileId);
                        Assert.AreEqual(CloudContentAvailability.OnlineOnly, retainedSource.ContentAvailability);
                        CollectionAssert.AreEqual(batch.FinalCursor.ToArray(), (await engine.ReadCursorAsync(registration.InstanceId, deadline.Token))!.Value.ToArray());
                        var originalStart = await catalog.ReadNamespaceMemberMoveStartAsync(operation, deadline.Token);
                        Assert.AreEqual(2, originalStart!.Version); Assert.AreEqual("v1", originalStart.OriginalOfficialRemoteRevision);
                        Assert.AreEqual(0, provider.Reads);
                        TestContext.WriteLine($"ManagedRemoteStaleBatch: caseOnly={caseOnly}; twoSdkOwners=True; originalOfficialRevision=True; " +
                            "sdkConflictRetained=True; sourceBindingUnchanged=True; nativeRedispatch=False; adapterCursorAfterRejection=True; hydration=False.");
                        continue;
                    }
                    var completion = await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token);
                    Assert.IsNotNull(completion);
                    Assert.AreEqual(targetPath, (await catalog.ReadNamespaceMemberLocationAsync(intent.MemberId, deadline.Token)).RelativePath);
                    Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                    CollectionAssert.AreEqual(batch.FinalCursor.ToArray(), (await engine.ReadCursorAsync(registration.InstanceId, deadline.Token))!.Value.ToArray());
                    Assert.AreEqual(firstMember.LocalObject.LocalFileId, (await fileSystem.GetFile(targetPath).InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                    Assert.AreEqual(CloudContentAvailability.OnlineOnly, (await fileSystem.GetFile(targetPath).InspectAsync(deadline.Token)).ContentAvailability);
                    if (caseOnly)
                    {
                        var names = new List<string>();
                        await foreach (var item in fileSystem.GetDirectory("Docs").EnumerateLocalChildrenAsync(CloudDirectoryEnumerationOptions.Default, deadline.Token))
                            names.Add(item.RelativePath.Replace('\\', '/'));
                        Assert.Contains(targetPath, names); Assert.DoesNotContain(sourcePath, names);
                    }
                    else Assert.IsFalse((await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token)).Exists);
                    Assert.AreEqual(0, provider.Reads);
                    TestContext.WriteLine($"ManagedRemoteBatch: twoSdkOwners=True; recoveredUnmovedStart={stopBeforeSdk}; originalSdkCommitBeforeProductSettlement={!stopBeforeSdk}; " +
                        "originalBatchAndAdmissionRetained=True; nativeRedispatch=False; adapterCursorAfterSettlement=True; hydration=False; defaultHostIntegrated=False.");
                    continue;
                }
                else if (settleRejected)
                {
                    Assert.IsNotNull(firstRejection); Assert.IsNotNull(peer);
                    Assert.AreEqual(intent, await catalog.ReadNamespaceMemberMoveAsync(operation, deadline.Token));
                    Assert.AreEqual(firstRejection, await coordinator.RejectRemoteAsync(operation, registration.InstanceId, batch, 0,
                        firstRejection.ConflictId, deadline.Token));
                    Assert.AreEqual(peer, await catalog.ReadPendingNamespaceMemberMoveAsync(registration.RootId, deadline.Token));
                    Assert.AreEqual(sourcePath, (await catalog.ReadNamespaceMemberLocationAsync(intent.MemberId, deadline.Token)).RelativePath);
                    Assert.IsNotNull(await fileSystem.GetRemoteConflictAsync(firstRejection.ConflictId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(operation, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(operation, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(operation, deadline.Token));
                    await using (var retainedRows = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        foreach (Guid id in originalJournal) Assert.IsNotNull(await retainedRows.Operations.GetAsync(id, deadline.Token));
                        await retainedRows.RollbackAsync(deadline.Token);
                    }
                    var unchanged = await fileSystem.GetFile(sourcePath).InspectAsync(deadline.Token);
                    Assert.AreEqual(firstMember.LocalObject.LocalFileId, unchanged.LocalBinding!.LocalFileId);
                    Assert.AreEqual(CloudContentAvailability.OnlineOnly, unchanged.ContentAvailability);
                    Assert.AreEqual(0, provider.Reads);
                    Assert.IsNull(await new MirrorPulseRemoteBatchCoordinator(fileSystem, state).ReadCursorAsync(registration.InstanceId, deadline.Token));
                    TestContext.WriteLine($"OriginalRemoteRejection: caseOnly={caseOnly}; twoSdkOwners=True; originalIntentAndStart=True; " +
                        "originalJournalRetained=True; noNativeMove=True; noFalseCompletion=True; peerAdmissionRetained=True; " +
                        "sdkConflictUnresolved=True; adapterCursorPublished=False; hydration=False; defaultHostIntegrated=False.");
                    continue;
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
                    $"publicSdkExecution=True; publicDirectoryReconciliation={directoryMove}; feedStartedAfterProtection={!directoryMove}; " +
                    "productPositionSeparate=True; adapterCursorPublished=False; hydration=False; sameRoleAcrossSdkOwners=True; defaultHostIntegrated=False.");
            }
        }
        finally
        {
            try
            {
                await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!,
                    ["Docs", "Docs/left", "Docs/right", "Docs/right/moved"],
                    ["Docs/remote.bin", "Docs/REMOTE.bin", "Docs/right/remote.bin", "Docs/left/cold.bin", "Docs/right/moved/cold.bin"]);
            }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
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
