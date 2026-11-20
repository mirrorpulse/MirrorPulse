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
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteFileDeleteSettlesMemberAfterSdkResultGap() => RunExplicitRemoteDeleteAsync(false, 0, false, false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteFileDeleteRecoversLostSdkConflictCommit() => RunExplicitRemoteDeleteAsync(false, 0, true, false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteEmptyDirectoryDeleteSettlesMemberAfterSdkResultGap() => RunExplicitRemoteDeleteAsync(true, 0, false, false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteEmptyDirectoryDeleteRecoversLostSdkConflictCommit() => RunExplicitRemoteDeleteAsync(true, 0, true, false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteDeleteConflictRetainsChildAndDurableConflictAcrossOwners() => RunOriginalRemoteDeleteGapAsync(false, false, true);

    private async Task RunOriginalRemoteDeleteGapAsync(bool directoryItem, bool loseProgressCommit, bool conflict, bool recreate = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.original-batch-delete"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]); var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        var identity = router.CreateFileIdentity(root.InstanceId, "docs", "retire-original", "v1");
        var kind = directoryItem ? CloudItemKind.Directory : CloudItemKind.File;
        var metadata = directoryItem ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        var birthBatch = new CloudRemoteChangeBatch(root.InstanceId + "/birth", ReadOnlyMemory<byte>.Empty,
            [new("birth", directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                identity.RemoteId, "v1", kind, "Docs\\retire", identity.ItemId, length: directoryItem ? null : 4, metadata: metadata,
                cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var deleteBatch = new CloudRemoteChangeBatch(root.InstanceId + "/delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, identity.RemoteId, "v2", kind, "Docs\\retire", identity.ItemId,
                previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var progressFault = new MirrorPulseRemoteProgressCommitFault(deleteBatch);
        MirrorPulseNamespaceRetirementIntent original = null!;
        MirrorPulseNamespaceMemberRegistration originalMember = null!;
        CloudPlaceholderIdentity successor = null!;
        MirrorPulseNamespaceMemberRegistration successorMember = null!;
        MirrorPulseNamespaceRemoteRetirementOutcome originalOutcome = null!;
        Guid decisionCommand = Guid.NewGuid();
        MirrorPulseRemoteConflictActionReceipt originalReceipt = null!;
        string originalDecision = string.Empty;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, caller.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < (recreate ? 3 : 2); owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(owner == 0 && loseProgressCommit ? progressFault.Wrap(state) : state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                    retirementCheckpoint: owner == 0 && !loseProgressCommit ? (point, _) => point == MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply
                        ? ValueTask.FromException(new IOException("Synthetic interruption after the original SDK delete result.")) : ValueTask.CompletedTask : null);
                var center = new MirrorPulseConflictCenter();
                var projector = recreate ? new MirrorPulseRemoteConflictProjector(fileSystem, catalog, center,
                    new MirrorPulseConflictNotificationBridge((_, _) => ValueTask.CompletedTask)) : null;
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, projector, driver);
                if (owner == 0)
                {
                    var birth = await coordinator.ApplyAsync(root.InstanceId, birthBatch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, birth.Status);
                    var originalBirth = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, birthBatch.BatchId, 0, deadline.Token))!;
                    originalMember = (await catalog.ReadNamespaceMemberAsync(originalBirth.OperationId, deadline.Token))!;
                    if (conflict && !directoryItem)
                    {
                        await execution.RunNamespaceOperationAsync(async () =>
                            await fileSystem.GetFile("Docs/retire").SetInSyncAsync(false, cancellationToken: deadline.Token));
                        Assert.AreEqual(CloudSynchronizationState.NotInSync,
                            (await fileSystem.GetFile("Docs/retire").InspectAsync(deadline.Token)).SynchronizationState);
                    }
                    await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(root.InstanceId, deleteBatch, cancellationToken: deadline.Token).AsTask());
                    var pending = (await catalog.ReadNamespaceRemoteRetirementAsync(root.InstanceId, deleteBatch.BatchId, 0, deadline.Token))!;
                    original = pending.Intent;
                    Assert.AreEqual(originalMember.LocalObject, original.LocalObject);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Prepared, pending.Phase);
                    Assert.IsNotNull(pending.StartedAt);
                    CloudItem deletedItem = directoryItem ? fileSystem.GetDirectory("Docs/retire") : fileSystem.GetFile("Docs/retire");
                    Assert.AreEqual(conflict, (await deletedItem.InspectAsync(deadline.Token)).Exists);
                    Assert.IsTrue(await catalog.CanResumeNamespaceRemoteRetirementAsync(original, deadline.Token));
                    Assert.IsTrue(await new MirrorPulseRemoteNamespaceFence(router, catalog).CanReplayAsync(root, deleteBatch, deadline.Token));
                    CollectionAssert.AreEqual(new byte[] { 1 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    Assert.AreEqual(!conflict, (await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!.IsTombstone);
                    var progress = (await transaction.RemoteBatches.GetAsync(deleteBatch.BatchId, deadline.Token))!;
                    Assert.AreEqual(loseProgressCommit ? 0 : 1, progress.AppliedEntryCount);
                    await transaction.RollbackAsync(deadline.Token);
                    if (loseProgressCommit) Assert.AreEqual(1, progressFault.Failures);
                }
                else
                {
                    if (owner == 2)
                    {
                        CollectionAssert.AreEqual(new byte[] { 3 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                        await using (var history = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                        {
                            var applied = (await history.RemoteBatches.GetAsync(deleteBatch.BatchId, deadline.Token))!;
                            Assert.AreEqual(CloudRemoteBatchStatus.Applied, applied.Status);
                            Assert.AreEqual(deleteBatch.Changes.Count, applied.AppliedEntryCount);
                            CollectionAssert.AreEqual(deleteBatch.FinalCursor.ToArray(), applied.Cursor.ToArray());
                            CollectionAssert.AreEqual(deleteBatch.Fingerprint.ToArray(), applied.Fingerprint.ToArray());
                            await history.RollbackAsync(deadline.Token);
                        }
                        var stale = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.ApplyAsync(root.InstanceId, deleteBatch,
                            cancellationToken: deadline.Token).AsTask());
                        Assert.AreEqual("The Adapter batch does not follow its durable CfSharp cursor.", stale.Message);
                        // The original SDK receipt is historical. It cannot become today's
                        // product cursor or authorize another native deletion of the successor.
                        var historical = await fileSystem.ApplyRemoteChangesAsync(deleteBatch, cancellationToken: deadline.Token);
                        Assert.AreEqual(CloudRemoteBatchStatus.Applied, historical.Status);
                        Assert.IsFalse(historical.RequiresRetry);
                        Assert.AreEqual(deleteBatch.Changes.Count, historical.AppliedEntryCount);
                        CollectionAssert.AreEqual(deleteBatch.FinalCursor.ToArray(), historical.SafeCursor.ToArray());
                        CollectionAssert.AreEqual(new byte[] { 3 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                        var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
                        var restored = await resolver.ResolveAsync(root, "retire-original", "later-v3", deadline.Token);
                        Assert.AreEqual(successor.ItemId, restored.ItemId); Assert.AreEqual(successor.RemoteId, restored.RemoteId);
                        await resolver.RequireCurrentAsync(root, restored, deadline.Token);
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(root, identity, deadline.Token).AsTask());
                        Assert.AreEqual(successorMember, await catalog.ReadNamespaceMemberChildAsync(original.RemoteEntry!.Parent.Context.Member.MemberId, "retire", deadline.Token));
                        CloudItem currentItem = directoryItem ? fileSystem.GetDirectory("Docs/retire") : fileSystem.GetFile("Docs/retire");
                        var native = await currentItem.InspectAsync(deadline.Token);
                        Assert.IsTrue(native.Exists); Assert.AreEqual(successor.ItemId, native.ItemId);
                        Assert.AreEqual(successor.ItemId, CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span).ItemId);
                        Assert.AreEqual(successorMember.LocalObject.LocalFileId, native.LocalBinding!.LocalFileId);
                        Assert.AreEqual(originalOutcome, await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token));
                        Assert.AreEqual(originalReceipt, await catalog.ReadRemoteConflictActionReceiptAsync(decisionCommand, deadline.Token));
                        Assert.AreEqual(originalDecision, System.Text.Json.JsonSerializer.Serialize(
                            await catalog.ReadRemoteDeleteResolutionAsync(decisionCommand, deadline.Token)));
                        await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        Assert.IsTrue((await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!.IsTombstone);
                        Assert.IsFalse((await transaction.Items.GetByItemIdAsync(successor.ItemId, deadline.Token))!.IsTombstone);
                        Assert.AreEqual(successor.ItemId, (await transaction.Items.GetByRelativePathAsync(birthBatch.Changes[0].RelativePath, deadline.Token))!.ItemId);
                        CollectionAssert.AreEqual(deleteBatch.Fingerprint.ToArray(), (await transaction.RemoteBatches.GetAsync(deleteBatch.BatchId, deadline.Token))!.Fingerprint.ToArray());
                        await transaction.RollbackAsync(deadline.Token);
                        Assert.AreEqual(0, provider.Reads);
                        continue;
                    }
                    var result = await coordinator.ApplyAsync(root.InstanceId, deleteBatch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status); Assert.IsFalse(result.RequiresRetry);
                    var terminal = (await catalog.ReadNamespaceRemoteRetirementAsync(root.InstanceId, deleteBatch.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(original, terminal.Intent);
                    Assert.AreEqual(conflict ? MirrorPulseNamespaceRetirementPhase.RemoteRejected : MirrorPulseNamespaceRetirementPhase.Completed, terminal.Phase);
                    var outcome = (await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token))!;
                    Assert.AreEqual(!conflict, outcome.NativeMissing); Assert.AreEqual(!conflict, outcome.OfficialTombstone);
                    Assert.AreEqual(conflict, outcome.ConflictId is not null);
                    Assert.AreEqual(originalMember, await catalog.ReadNamespaceMemberAsync(originalMember.MemberId, deadline.Token));
                    Assert.AreEqual(conflict ? originalMember : null,
                        await catalog.ReadNamespaceMemberChildAsync(original.RemoteEntry!.Parent.Context.Member.MemberId, "retire", deadline.Token));
                    if (conflict)
                    {
                        var records = await fileSystem.ListRemoteConflictsAsync(deadline.Token);
                        Assert.IsTrue(records.Any(record => record.ConflictId == outcome.ConflictId));
                        Assert.AreEqual(original.LocalObject.LocalFileId,
                            (await (directoryItem ? (CloudItem)fileSystem.GetDirectory("Docs/retire") : fileSystem.GetFile("Docs/retire"))
                                .InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                    }
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    CollectionAssert.AreEqual(deleteBatch.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, deleteBatch, cancellationToken: deadline.Token)).Status);
                    Assert.AreEqual(outcome, await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token));
                    Assert.AreEqual(0, provider.Reads);
                    if (recreate)
                    {
                        originalOutcome = outcome;
                        var preserved = (await catalog.ReadRemoteConflictProjectionsAsync(deadline.Token)).Single(record => record.ConflictId == outcome.ConflictId);
                        var deletion = new MirrorPulseRemoteDeleteResolutionCoordinator(fileSystem, state, catalog, router, execution, actor);
                        var actions = MirrorPulseRemoteConflictActions.For(fileSystem, new(paths), catalog, center, deletion.ResolveAsync);
                        Assert.IsTrue((await actions.ApplyAsync(decisionCommand, preserved, MirrorPulseConflictAction.KeepRemote,
                            cancellationToken: deadline.Token)).Resolved);
                        originalReceipt = (await catalog.ReadRemoteConflictActionReceiptAsync(decisionCommand, deadline.Token))!;
                        Assert.IsNotNull(originalReceipt);
                        var resolved = (await catalog.ReadRemoteDeleteResolutionAsync(decisionCommand, deadline.Token))!;
                        Assert.AreEqual(2, resolved.Plan.Version); Assert.AreEqual(root.UniquenessKey, resolved.Plan.RootKey);
                        Assert.IsNotNull(resolved.CompletedAt);
                        originalDecision = System.Text.Json.JsonSerializer.Serialize(resolved);
                        Assert.AreEqual(outcome, await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token));
                        successor = await new MirrorPulseRemoteIdentityResolver(catalog).ResolveAsync(root, "retire-original", "later-v3", deadline.Token);
                        Assert.AreNotEqual(identity.ItemId, successor.ItemId); Assert.AreNotEqual(identity.RemoteId, successor.RemoteId);
                        Assert.AreEqual(3, (await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, identity.ItemId, deadline.Token))!.Version);
                        var later = new CloudRemoteChangeBatch(root.InstanceId + "/later-birth", deleteBatch.FinalCursor,
                            [new("later-birth", directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                                successor.RemoteId, "later-v3", kind, "Docs/retire", successor.ItemId, length: directoryItem ? null : 4,
                                metadata: metadata, cursorAfter: new byte[] { 3 })], new byte[] { 3 });
                        Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, later, cancellationToken: deadline.Token)).Status);
                        var laterBirth = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, later.BatchId, 0, deadline.Token))!;
                        successorMember = (await catalog.ReadNamespaceMemberAsync(laterBirth.OperationId, deadline.Token))!;
                        Assert.AreNotEqual(originalMember.MemberId, successorMember.MemberId);
                        Assert.AreNotEqual(originalMember.LocalObject, successorMember.LocalObject);
                        Assert.AreEqual(successor.ItemId, (await catalog.ReadNamespaceMemberIdentityAsync(successorMember.MemberId, deadline.Token)).ItemId);
                        Assert.AreEqual(outcome, await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token));
                    }
                }
            }
            TestContext.WriteLine($"OriginalRemoteDelete: directory={directoryItem}; lostSdkProgressCommit={loseProgressCommit}; durableConflictRetained={conflict}; " +
                "originalNativeBindingBeforeSdk=True; sdkOwnsDeletion=True; noPredeletion=True; " +
                $"twoProductAndSdkOwners=True; actualTombstone={!conflict}; nameSettledBeforeCursor=True; sameRole=True; " +
                "hydration=False; recursiveDelete=False; newRoleRecovery=False; defaultHostIntegrated=False.");
            if (recreate) TestContext.WriteLine($"RemoteDeletionGeneration: directory={directoryItem}; threeProductAndSdkOwners=True; " +
                $"originalItem={identity.ItemId:D}; successorItem={successor.ItemId:D}; differentNativeMember=True; oldCallbackRejected=True; " +
                "originalDeleteReplayPreservedSuccessor=True; originalSdkTombstoneAndBatchRetained=True; " +
                "defaultDeleteConflictPreserved=True; explicitKeepRemote=True; separateDecisionReceiptRetained=True; " +
                "staleProductCursorRejected=True; sdkHistoricalReplay=True; currentCursorPreserved=True; providerReads=0.");
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", "Docs/retire"] : ["Docs"], directoryItem ? [] : ["Docs/retire"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
