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
    public Task NativeExplicitRemoteTreeDeleteRecoversItsSdkResultBeforeFollowingWorkAndCursor() => RunExplicitRemoteDeleteAsync(true, 1, false, false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteTreeDeleteYieldsAfter64MembersAndReopensBeforeCommandReceipt() => RunExplicitRemoteDeleteAsync(true, 64, false, true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteTreeDeletePreservesDirtyDescendantAndOriginalConflict() => RunOriginalRemoteTreeDeleteAsync(false, true);

    private async Task RunOriginalRemoteTreeDeleteAsync(bool pageTurn, bool conflict)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.original-tree-delete"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]); var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard(); int fileCount = pageTurn ? 63 : 1;
        var identities = Enumerable.Range(0, fileCount + 2).Select(index => router.CreateFileIdentity(root.InstanceId, "docs", "original-" + index, "v1")).ToArray();
        var directoryMetadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().Build(); var fileMetadata = CloudPlaceholderMetadata.CreateFileBuilder().Build();
        string[] directories = ["Docs", "Docs/retire", "Docs/retire/nested", "Docs/after"];
        string[] files = Enumerable.Range(0, fileCount).Select(index => "Docs/retire/nested/file-" + index).ToArray();
        var creations = new List<CloudRemoteChange>
        {
            new("tree", CloudRemoteChangeKind.DirectoryUpsert, identities[0].RemoteId, "v1", CloudItemKind.Directory,
                "Docs\\retire", identities[0].ItemId, metadata: directoryMetadata),
            new("nested", CloudRemoteChangeKind.DirectoryUpsert, identities[1].RemoteId, "v1", CloudItemKind.Directory,
                "Docs\\retire\\nested", identities[1].ItemId, metadata: directoryMetadata)
        };
        for (int index = 0; index < fileCount; index++) creations.Add(new("file-" + index, CloudRemoteChangeKind.FileUpsert,
            identities[index + 2].RemoteId, "v1", CloudItemKind.File, files[index], identities[index + 2].ItemId, length: 4, metadata: fileMetadata));
        var setupBatch = new CloudRemoteChangeBatch(root.InstanceId + "/setup", ReadOnlyMemory<byte>.Empty, creations, new byte[] { 1 });
        var suffix = router.CreateFileIdentity(root.InstanceId, "docs", "after-delete", "v1");
        var batch = new CloudRemoteChangeBatch(root.InstanceId + "/delete-tree", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, identities[0].RemoteId, "v2", CloudItemKind.Directory, "Docs\\retire",
                identities[0].ItemId, previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 }),
            new("suffix", CloudRemoteChangeKind.DirectoryUpsert, suffix.RemoteId, "v1", CloudItemKind.Directory, "Docs\\after", suffix.ItemId,
                metadata: directoryMetadata, cursorAfter: new byte[] { 3 })], new byte[] { 3 });
        MirrorPulseNamespaceRetirementBatch original = null!; MirrorPulseNamespaceRemoteRetirementTreeStart start = null!;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await setup.ApplyRemoteChangesAsync(setupBatch, cancellationToken: deadline.Token)).Status);
                SetProtectedFixtureUserOwner(setup.Root, caller.User!);
                foreach (string path in directories.Take(3)) SetProtectedFixtureUserOwner(setup.GetDirectory(path), caller.User!);
                foreach (string path in files) SetProtectedFixtureUserOwner(setup.GetFile(path), caller.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                    retirementCheckpoint: owner == 0 && !pageTurn ? (point, _) => point == MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply
                        ? ValueTask.FromException(new IOException("Synthetic interruption after the original directory SDK return.")) : ValueTask.CompletedTask : null);
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                if (owner == 0)
                {
                    await using (var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        await transaction.Checkpoints.UpsertAsync(new(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(root.InstanceId), new byte[] { 1 }, DateTimeOffset.UtcNow), deadline.Token);
                        await transaction.CommitAsync(deadline.Token);
                    }
                    if (conflict) await execution.RunNamespaceOperationAsync(async () =>
                        await fileSystem.GetFile(files[0]).SetInSyncAsync(false, cancellationToken: deadline.Token));
                    if (pageTurn)
                    {
                        var pending = await Assert.ThrowsExactlyAsync<MirrorPulseRemoteTreeSettlementPendingException>(() =>
                            coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token).AsTask());
                        Assert.AreEqual(1, pending.RemainingMembers);
                    }
                    else await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token).AsTask());
                    original = (await catalog.ReadNamespaceRemoteRetirementTreeAsync(root.InstanceId, batch.BatchId, 0, deadline.Token))!;
                    start = (await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId, deadline.Token))!;
                    Assert.HasCount(fileCount + 2, original.Entries);
                    Assert.AreEqual(conflict, (await fileSystem.GetDirectory("Docs/retire").InspectAsync(deadline.Token)).Exists);
                    Assert.IsFalse((await fileSystem.GetDirectory("Docs/after").InspectAsync(deadline.Token)).Exists);
                    Assert.AreEqual(pageTurn ? 0 : fileCount + 1, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(original.OperationId, deadline.Token))!.NextOrdinal);
                    Assert.IsTrue(await new MirrorPulseRemoteNamespaceFence(router, catalog, true).CanReplayAsync(root, batch, deadline.Token));
                    CollectionAssert.AreEqual(new byte[] { 1 }, (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                }
                else
                {
                    var result = await coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status); Assert.IsFalse(result.RequiresRetry);
                    var retained = (await catalog.ReadNamespaceRemoteRetirementTreeAsync(root.InstanceId, batch.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(original.OperationId, retained.OperationId); Assert.AreEqual(original.RemoteTreeFingerprint, retained.RemoteTreeFingerprint);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, retained.Phase);
                    Assert.AreEqual(start, await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId, deadline.Token));
                    var outcome = (await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(original.OperationId, deadline.Token))!;
                    Assert.AreEqual(conflict ? "Conflict" : pageTurn ? "Applied" : "CommittedPrefix", outcome.EntryStatus);
                    Assert.AreEqual(conflict, outcome.ConflictId is not null);
                    Assert.IsNotNull((await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(original.OperationId, deadline.Token))!.CompletedAt);
                    var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(original.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value, deadline.Token);
                    Assert.AreEqual(conflict ? 0 : fileCount + 2, inventory.RetiredTreeMembers?.Count ?? 0);
                    foreach (var identity in identities)
                    {
                        await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        var projected = (await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!;
                        Assert.AreEqual(!conflict, projected.IsTombstone);
                        Assert.AreEqual(!conflict && identity.ItemId == identities[0].ItemId ? "v2" : "v1", projected.RemoteRevision);
                        await transaction.RollbackAsync(deadline.Token);
                    }
                    if (conflict)
                    {
                        Assert.IsTrue((await fileSystem.ListRemoteConflictsAsync(deadline.Token)).Any(record => record.ConflictId == outcome.ConflictId));
                        Assert.IsTrue((await fileSystem.GetFile(files[0]).InspectAsync(deadline.Token)).Exists);
                    }
                    Assert.IsTrue((await fileSystem.GetDirectory("Docs/after").InspectAsync(deadline.Token)).Exists);
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    CollectionAssert.AreEqual(batch.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token)).Status);
                    Assert.AreEqual(outcome, await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(original.OperationId, deadline.Token));
                    Assert.AreEqual(0, provider.Reads);
                }
            }
            TestContext.WriteLine($"OriginalRemoteTreeDelete: originalMembers={fileCount + 2}; boundedPageTurn={pageTurn}; dirtyDescendantConflict={conflict}; " +
                "singleOriginalSdkDirectoryDelete=True; noControlledPredelete=True; twoProductAndSdkOwners=True; sameRole=True; " +
                "originalStartRetained=True; suffixAfterSettlement=True; memberNamesBeforeAdapterCursor=True; descendantRevisionsPreserved=True; " +
                "hydration=False; newRoleRecovery=False; defaultHostIntegrated=False.");
        }
        finally
        {
            try
            {
                foreach (string[] page in files.Chunk(32))
                    await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, [], page);
                await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directories, []);
            }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
