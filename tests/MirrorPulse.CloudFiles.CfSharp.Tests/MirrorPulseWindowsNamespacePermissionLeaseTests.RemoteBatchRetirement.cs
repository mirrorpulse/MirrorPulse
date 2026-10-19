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

    private async Task RunOriginalRemoteDeleteGapAsync(bool directoryItem, bool loseProgressCommit, bool conflict)
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
            for (int owner = 0; owner < 2; owner++)
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
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                if (owner == 0)
                {
                    var birth = await coordinator.ApplyAsync(root.InstanceId, birthBatch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, birth.Status);
                    var originalBirth = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, birthBatch.BatchId, 0, deadline.Token))!;
                    originalMember = (await catalog.ReadNamespaceMemberAsync(originalBirth.OperationId, deadline.Token))!;
                    if (conflict)
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
                            (await fileSystem.GetFile("Docs/retire").InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                    }
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    CollectionAssert.AreEqual(deleteBatch.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, deleteBatch, cancellationToken: deadline.Token)).Status);
                    Assert.AreEqual(outcome, await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId, deadline.Token));
                    Assert.AreEqual(0, provider.Reads);
                }
            }
            TestContext.WriteLine($"OriginalRemoteDelete: directory={directoryItem}; lostSdkProgressCommit={loseProgressCommit}; durableConflictRetained={conflict}; " +
                "originalNativeBindingBeforeSdk=True; sdkOwnsDeletion=True; noPredeletion=True; " +
                $"twoProductAndSdkOwners=True; actualTombstone={!conflict}; nameSettledBeforeCursor=True; sameRole=True; " +
                "hydration=False; recursiveDelete=False; newRoleRecovery=False; defaultHostIntegrated=False.");
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", "Docs/retire"] : ["Docs"], directoryItem ? [] : ["Docs/retire"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
