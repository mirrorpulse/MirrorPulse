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
    public Task NativeMissingOriginalRemoteDeleteRetainsActualSdkConflictWithoutInventingMemberRetirementAcrossOwners() =>
        RunMissingOriginalRemoteDeleteAsync(false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeMiddleMissingRemoteDeleteSettlesOriginalRejectionBeforeLaterSdkEntryAndCursorAcrossOwners() =>
        RunMissingOriginalRemoteDeleteAsync(true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeMissingRemoteDeleteRecoversOriginalAdmissionBeforeSdkAcrossFreshRoles() =>
        RunMissingOriginalRemoteDeleteAsync(false, MirrorPulseRemoteDeleteRejectionCheckpoint.AfterAdmission, true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeMissingRemoteDeleteRecoversActualSdkConflictBeforeProductResultAcrossFreshRoles() =>
        RunMissingOriginalRemoteDeleteAsync(false, MirrorPulseRemoteDeleteRejectionCheckpoint.AfterSdkApply, true);

    private async Task RunMissingOriginalRemoteDeleteAsync(bool ordered,
        MirrorPulseRemoteDeleteRejectionCheckpoint? interruption = null, bool freshRole = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.missing-original-delete"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]); var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var identity = router.CreateFileIdentity(root.InstanceId, "docs", "old-address", "v1");
        var control = new CloudRemoteChangeBatch(root.InstanceId + "/unrelated-control", ReadOnlyMemory<byte>.Empty,
            [new("different-original-change", CloudRemoteChangeKind.Delete, identity.RemoteId, "v2", CloudItemKind.File,
                "Docs/old.bin", identity.ItemId, cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var deletion = new CloudRemoteChange("original-delete", CloudRemoteChangeKind.Delete, identity.RemoteId, "v2", CloudItemKind.File,
            "Docs/old.bin", identity.ItemId, cursorAfter: new byte[] { 2 });
        var prefix = router.CreateFileIdentity(root.InstanceId, "docs", "prefix", "v1");
        var tail = router.CreateFileIdentity(root.InstanceId, "docs", "tail", "v1");
        var original = new CloudRemoteChangeBatch(root.InstanceId + "/missing-original", ReadOnlyMemory<byte>.Empty,
            ordered ? [new("prefix", CloudRemoteChangeKind.FileUpsert, prefix.RemoteId, "v1", CloudItemKind.File,
                "Docs/prefix.bin", prefix.ItemId, length: 4, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: new byte[] { 1 }),
                deletion, new("tail", CloudRemoteChangeKind.FileUpsert, tail.RemoteId, "v1", CloudItemKind.File,
                    "Docs/tail.bin", tail.ItemId, length: 4, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: new byte[] { 3 })]
                : [deletion], ordered ? new byte[] { 3 } : new byte[] { 2 });
        int deleteOrdinal = ordered ? 1 : 0;
        Guid controlConflict = Guid.Empty, originalConflict = Guid.Empty, parentMember = Guid.Empty;
        int completeOwners = 0;
        string? originalRole = null;
        MirrorPulseNamespaceMemberProtectionSnapshot originalParent = null!;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, caller.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
            }
            await using var sharedExecution = freshRole ? null : new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < 2; owner++)
            {
                await using var newExecution = freshRole ? new MirrorPulseNamespaceExecutionSession() : null;
                var execution = newExecution ?? sharedExecution!;
                if (owner == 0) originalRole = execution.RoleSid.Value;
                else if (freshRole) Assert.AreNotEqual(originalRole, execution.RoleSid.Value);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var pendingOriginal = await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token);
                var parent = pendingOriginal?.Admission.RootMemberId ?? (await new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, execution, actor)
                    .InspectAsync("Docs", true, deadline.Token)).MemberId;
                if (owner == 0) parentMember = parent;
                else Assert.AreEqual(parentMember, parent);
                var before = owner == 0 ? (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent, deadline.Token))! : originalParent;
                if (owner == 0) originalParent = before;
                var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                    rejectionCheckpoint: owner == 0 && interruption is not null ? (point, _) => point == interruption
                        ? ValueTask.FromException(new IOException("Synthetic interruption at the original preserving Delete boundary.")) : ValueTask.CompletedTask : null);
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                if (owner == 0)
                {
                    var publicControl = await fileSystem.ApplyRemoteChangesAsync(control, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, publicControl.Status);
                    Assert.AreEqual(CloudRemoteApplyEntryStatus.Conflict, publicControl.Entries[0].Status);
                    Assert.AreEqual(CloudRemoteConflictReason.MissingItem, publicControl.Entries[0].Conflict!.Reason);
                    controlConflict = publicControl.ConflictIds.Single();
                    Assert.IsNull(await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token));
                }
                if (owner == 0 && interruption is not null)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(root.InstanceId, original,
                        cancellationToken: deadline.Token).AsTask());
                    var retained = (await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token))!;
                    Assert.IsNull(retained.Outcome);
                    Assert.Contains(root.RootId, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                    var fence = new MirrorPulseRemoteNamespaceFence(router, catalog);
                    Assert.IsFalse(await fence.CanPollAsync(root, deadline.Token));
                    Assert.IsTrue(await fence.CanReplayAsync(root, original, deadline.Token));
                    var foreignBatch = new CloudRemoteChangeBatch(root.InstanceId + "/foreign-original", original.InitialCursor, original.Changes, original.FinalCursor);
                    Assert.IsFalse(await fence.CanReplayAsync(root, foreignBatch, deadline.Token));
                    Assert.IsNull(await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token));
                    await using (var read = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        var progress = await read.RemoteBatches.GetAsync(original.BatchId, deadline.Token);
                        await read.RollbackAsync(deadline.Token);
                        if (interruption == MirrorPulseRemoteDeleteRejectionCheckpoint.AfterAdmission) Assert.IsNull(progress);
                        else
                        {
                            Assert.IsNotNull(progress); Assert.AreEqual(1, progress.AppliedEntryCount);
                            var pendingConflict = (await fileSystem.ListRemoteConflictsAsync(deadline.Token)).Single(value => value.Conflict.Change.ChangeId == "original-delete");
                            originalConflict = pendingConflict.ConflictId;
                        }
                    }
                    completeOwners++; continue;
                }
                var applied = await coordinator.ApplyAsync(root.InstanceId, original, cancellationToken: deadline.Token);
                if (ordered && owner == 0)
                {
                    Assert.AreEqual(1, applied.AppliedEntryCount); Assert.AreEqual(CloudRemoteBatchStatus.Applying, applied.Status);
                    Assert.IsNull(await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token));
                    Assert.IsNull(await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token));
                    applied = await coordinator.ApplyAsync(root.InstanceId, original, cancellationToken: deadline.Token);
                    Assert.AreEqual(2, applied.AppliedEntryCount); Assert.AreEqual(CloudRemoteBatchStatus.Applying, applied.Status);
                    var settled = (await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token))!;
                    Assert.IsNotNull(settled.Outcome);
                    Assert.IsFalse((await fileSystem.GetFile("Docs/tail.bin").InspectAsync(deadline.Token)).Exists);
                    Assert.IsNull(await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token));
                    applied = await coordinator.ApplyAsync(root.InstanceId, original, cancellationToken: deadline.Token);
                }
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, applied.Status);
                Assert.AreEqual(original.Changes.Count, applied.AppliedEntryCount); Assert.IsFalse(applied.RequiresRetry);
                var records = await fileSystem.ListRemoteConflictsAsync(deadline.Token);
                Assert.HasCount(2, records);
                var own = records.Single(value => value.Conflict.Change.ChangeId == "original-delete");
                Assert.AreEqual(CloudRemoteConflictReason.MissingItem, own.Conflict.Reason);
                Assert.AreNotEqual(controlConflict, own.ConflictId);
                if (originalConflict == Guid.Empty) originalConflict = own.ConflictId;
                else Assert.AreEqual(originalConflict, own.ConflictId);
                var rejection = (await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token))!;
                Assert.IsNotNull(rejection); Assert.IsNotNull(rejection.Outcome);
                Assert.AreEqual(own.ConflictId, rejection.Outcome.ConflictId);
                Assert.AreEqual(original.BatchId, rejection.Admission.BatchId);
                Assert.AreEqual(root.RootId, rejection.Admission.RootId);
                Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementAsync(root.InstanceId, original.BatchId, deleteOrdinal, deadline.Token));
                Assert.AreEqual(before, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent, deadline.Token));
                Assert.IsFalse((await fileSystem.GetFile("Docs/old.bin").InspectAsync(deadline.Token)).Exists);
                CollectionAssert.AreEqual(original.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                completeOwners++;
            }
            Assert.AreEqual(2, completeOwners);
        }
        finally
        {
            try
            {
                await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, ["Docs"],
                    ordered ? ["Docs/prefix.bin", "Docs/tail.bin"] : []);
            }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
