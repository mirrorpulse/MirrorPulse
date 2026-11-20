using System.Security.Principal;
using System.Text.Json;
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
    public Task NativeExplicitRemoteFileDeleteReconcilesLostReturnUnderANewRole() => RunExplicitRemoteDeleteAsync(false, 0, false, false, true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteDirectoryDeleteReconcilesLostReturnUnderANewRole() => RunExplicitRemoteDeleteAsync(true, 0, false, false, true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeExplicitRemoteTreeDeleteReconcilesLostReturnUnderANewRole() => RunExplicitRemoteDeleteAsync(true, 1, false, false, true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCompletedRemoteDeleteRecoversItsCommandReceiptAfterParentRoleRotation() => RunExplicitRemoteDeleteAsync(true, 1, false, false, true, true);

    private async Task RunExplicitRemoteDeleteAsync(bool directoryItem, int children, bool loseConflictCommit, bool pageTurn,
        bool rotateRole = false, bool loseCommandReceipt = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.explicit-remote-delete"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]); var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        var identities = Enumerable.Range(0, children + 1).Select(index => router.CreateFileIdentity(root.InstanceId, "docs", "explicit-" + index, "v1")).ToArray();
        var kind = directoryItem ? CloudItemKind.Directory : CloudItemKind.File;
        var fileMetadata = CloudPlaceholderMetadata.CreateFileBuilder().Build(); var directoryMetadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().Build();
        string[] files = directoryItem ? Enumerable.Range(0, children).Select(index => "Docs/retire/file-" + index).ToArray() : ["Docs/retire"];
        var creations = new List<CloudRemoteChange>
        {
            new("target", directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                identities[0].RemoteId, "v1", kind, "Docs/retire", identities[0].ItemId, length: directoryItem ? null : 4,
                metadata: directoryItem ? directoryMetadata : fileMetadata)
        };
        for (int index = 0; index < children; index++) creations.Add(new("child-" + index, CloudRemoteChangeKind.FileUpsert,
            identities[index + 1].RemoteId, "v1", CloudItemKind.File, files[index], identities[index + 1].ItemId, length: 4, metadata: fileMetadata));
        var setupBatch = new CloudRemoteChangeBatch(root.InstanceId + "/setup", ReadOnlyMemory<byte>.Empty, creations, new byte[] { 1 });
        var poll = new CloudRemoteChangeBatch(root.InstanceId + "/poll-delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, identities[0].RemoteId, "v2", kind, "Docs/retire", identities[0].ItemId,
                previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var suffixIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "after-delete", "v1");
        var suffix = new CloudRemoteChangeBatch(root.InstanceId + "/after-delete", new byte[] { 2 },
            [new("suffix", CloudRemoteChangeKind.DirectoryUpsert, suffixIdentity.RemoteId, "v1", CloudItemKind.Directory,
                "Docs/after", suffixIdentity.ItemId, metadata: directoryMetadata, cursorAfter: new byte[] { 3 })], new byte[] { 3 });
        Guid command = Guid.NewGuid(); MirrorPulseConflictRecord conflict = null!; MirrorPulseRemoteDeleteResolutionPlan originalPlan = null!;
        string originalPollEvidence = string.Empty; var commitFault = new MirrorPulseRemoteConflictCommitFault();
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
                SetProtectedFixtureUserOwner(setup.Root, caller.User!); SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
                if (directoryItem) SetProtectedFixtureUserOwner(setup.GetDirectory("Docs/retire"), caller.User!);
                foreach (string path in files) SetProtectedFixtureUserOwner(setup.GetFile(path), caller.User!);
            }
            await using var originalExecution = new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < 2; owner++)
            {
                await using var rotated = owner == 1 && rotateRole ? new MirrorPulseNamespaceExecutionSession() : null;
                var execution = rotated ?? originalExecution;
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                    .WithStateStore(owner == 0 && loseConflictCommit ? commitFault.Wrap(state) : state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var center = new MirrorPulseConflictCenter(); var projector = new MirrorPulseRemoteConflictProjector(fileSystem, catalog, center,
                    new MirrorPulseConflictNotificationBridge((_, _) => ValueTask.CompletedTask));
                var pollCoordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, projector,
                    new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor));
                var deletion = new MirrorPulseRemoteDeleteResolutionCoordinator(fileSystem, state, catalog, router, execution, actor,
                    owner == 0 && !loseConflictCommit && !pageTurn ? (point, _) =>
                        point == (loseCommandReceipt ? MirrorPulseRemoteRetirementCheckpoint.AfterSettlement : MirrorPulseRemoteRetirementCheckpoint.AfterSdkApply)
                            ? ValueTask.FromException(new IOException("Synthetic loss after the owned explicit Delete boundary.")) : ValueTask.CompletedTask : null);
                var actions = MirrorPulseRemoteConflictActions.For(fileSystem, new(paths), catalog, center, deletion.ResolveAsync);
                async Task<string> PollEvidenceAsync()
                {
                    if (directoryItem && children != 0)
                    {
                        var batch = (await catalog.ReadNamespaceRemoteRetirementTreeAsync(root.InstanceId, poll.BatchId, 0, deadline.Token))!;
                        Assert.AreEqual("Conflict", (await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId, deadline.Token))!.EntryStatus);
                        return JsonSerializer.Serialize(new
                        {
                            batch.OperationId,
                            batch.RemoteEntry,
                            batch.RemoteTreeFingerprint,
                            Outcome = await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId, deadline.Token)
                        });
                    }
                    var member = (await catalog.ReadNamespaceRemoteRetirementAsync(root.InstanceId, poll.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.RemoteRejected, member.Phase);
                    return JsonSerializer.Serialize(new
                    {
                        member.Intent,
                        member.Phase,
                        Outcome = await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(member.OperationId, deadline.Token)
                    });
                }
                if (owner == 0)
                {
                    await using (var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                    {
                        await transaction.Checkpoints.UpsertAsync(new(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(root.InstanceId), new byte[] { 1 }, DateTimeOffset.UtcNow), deadline.Token);
                        await transaction.CommitAsync(deadline.Token);
                    }
                    if (children >= 64)
                        _ = await Assert.ThrowsExactlyAsync<MirrorPulseRemoteTreeSettlementPendingException>(() =>
                            pollCoordinator.ApplyAsync(root.InstanceId, poll, cancellationToken: deadline.Token).AsTask());
                    var result = await pollCoordinator.ApplyAsync(root.InstanceId, poll, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status);
                    if (children < 64)
                    {
                        Assert.HasCount(1, result.ConflictIds);
                        Assert.AreEqual(CloudRemoteApplyEntryStatus.Conflict, result.Entries.Single().Status);
                    }
                    conflict = (await catalog.ReadRemoteConflictProjectionsAsync(deadline.Token)).Single();
                    Assert.AreEqual((await fileSystem.ListRemoteConflictsAsync(deadline.Token)).Single().ConflictId, conflict.ConflictId);
                    CloudItem target = directoryItem ? fileSystem.GetDirectory("Docs/retire") : fileSystem.GetFile("Docs/retire");
                    Assert.IsTrue((await target.InspectAsync(deadline.Token)).Exists);
                    foreach (var identity in identities)
                    {
                        await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        Assert.IsFalse((await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!.IsTombstone);
                        await transaction.RollbackAsync(deadline.Token);
                    }
                    originalPollEvidence = await PollEvidenceAsync();
                    if (loseConflictCommit) commitFault.Arm(conflict.ConflictId, identities[0].ItemId, "v2");
                    if (pageTurn)
                    {
                        var pending = await Assert.ThrowsExactlyAsync<MirrorPulseRemoteTreeSettlementPendingException>(() =>
                            actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote, cancellationToken: deadline.Token));
                        Assert.AreEqual(children + 1 - 64, pending.RemainingMembers);
                    }
                    else await Assert.ThrowsExactlyAsync<IOException>(() => actions.ApplyAsync(command, conflict,
                        MirrorPulseConflictAction.KeepRemote, cancellationToken: deadline.Token));
                    var retained = (await catalog.ReadRemoteDeleteResolutionAsync(command, deadline.Token))!; originalPlan = retained.Plan;
                    Assert.IsNotNull(retained.StartedAt); Assert.AreEqual(loseCommandReceipt, retained.CompletedAt is not null);
                    Assert.AreEqual(loseCommandReceipt ? -1 : pageTurn ? children - 64 : children, retained.NextOrdinal);
                    Assert.IsFalse((await target.InspectAsync(deadline.Token)).Exists);
                    Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(command, deadline.Token));
                    Assert.AreEqual(loseConflictCommit, await fileSystem.GetRemoteConflictAsync(conflict.ConflictId, deadline.Token) is not null);
                    Assert.AreEqual(!loseCommandReceipt, (await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    Assert.AreEqual(loseConflictCommit ? 1 : 0, commitFault.Failures);
                    if (!loseCommandReceipt)
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pollCoordinator.ApplyAsync(root.InstanceId, suffix, cancellationToken: deadline.Token).AsTask());
                    Assert.IsFalse((await fileSystem.GetDirectory("Docs/after").InspectAsync(deadline.Token)).Exists);
                }
                else
                {
                    var found = (await catalog.ReadPendingRemoteDeleteResolutionsAsync(root.RootId, limit: 1, token: deadline.Token)).Single();
                    Assert.AreEqual(command, found.Plan.Decision.CommandId);
                    if (rotateRole) Assert.AreNotEqual(originalPlan.Parent.Context.Protection.RoleSid, execution.RoleSid.Value);
                    if (loseCommandReceipt)
                    {
                        await using var recovered = await new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, router,
                            execution, actor, scheduler, permissions).RecoverRootAsync(Guid.NewGuid(), root.RootId, deadline.Token);
                        var currentParent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(originalPlan.Parent.Context.Member.MemberId, deadline.Token))!;
                        Assert.AreEqual(execution.RoleSid.Value, currentParent.Context.Protection.RoleSid);
                        Assert.AreNotEqual(originalPlan.Parent.Context.Protection.RoleSid, currentParent.Context.Protection.RoleSid);
                        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(command, deadline.Token));
                    }
                    var result = await actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote, cancellationToken: deadline.Token);
                    Assert.IsTrue(result.Resolved);
                    Assert.AreEqual(loseConflictCommit || pageTurn || loseCommandReceipt ? CloudRemoteApplyEntryStatus.Applied : CloudRemoteApplyEntryStatus.AlreadyApplied, result.CfSharpStatus);
                    var completed = (await catalog.ReadRemoteDeleteResolutionAsync(command, deadline.Token))!;
                    Assert.IsNotNull(completed.CompletedAt); Assert.AreEqual(-1, completed.NextOrdinal);
                    Assert.AreEqual(originalPlan.Decision, completed.Plan.Decision);
                    CollectionAssert.AreEqual(originalPlan.Entries.ToArray(), completed.Plan.Entries.ToArray());
                    foreach (var entry in completed.Plan.Entries)
                    {
                        Assert.AreEqual(entry.Original.Context.Member, await catalog.ReadNamespaceMemberAsync(entry.Original.Context.Member.MemberId, deadline.Token));
                        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberLocationAsync(entry.Original.Context.Member.MemberId, deadline.Token));
                        await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                        var item = (await transaction.Items.GetByItemIdAsync(entry.Original.Identity.ItemId!.Value, deadline.Token))!;
                        Assert.IsTrue(item.IsTombstone); Assert.AreEqual(entry.Ordinal == 0 ? "v2" : "v1", item.RemoteRevision);
                        await transaction.RollbackAsync(deadline.Token);
                    }
                    Assert.IsNull(await fileSystem.GetRemoteConflictAsync(conflict.ConflictId, deadline.Token));
                    Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command, deadline.Token))!.State);
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    Assert.HasCount(0, await catalog.ReadPendingRemoteDeleteResolutionsAsync(root.RootId, token: deadline.Token));
                    Assert.AreEqual(result, await actions.ApplyAsync(command, conflict, MirrorPulseConflictAction.KeepRemote, cancellationToken: deadline.Token));
                    if (rotateRole && !loseCommandReceipt)
                    {
                        await using var recovered = await new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, router,
                            execution, actor, scheduler, permissions).RecoverRootAsync(Guid.NewGuid(), root.RootId, deadline.Token);
                        Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, recovered.Completion.InventoryKind);
                    }
                    Assert.AreEqual(0, provider.Reads);
                }
                Assert.AreEqual(originalPollEvidence, await PollEvidenceAsync());
                CollectionAssert.AreEqual(poll.FinalCursor.ToArray(), (await pollCoordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                if (owner == 1)
                {
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, (await pollCoordinator.ApplyAsync(root.InstanceId, suffix, cancellationToken: deadline.Token)).Status);
                    Assert.IsTrue((await fileSystem.GetDirectory("Docs/after").InspectAsync(deadline.Token)).Exists);
                    CollectionAssert.AreEqual(suffix.FinalCursor.ToArray(), (await pollCoordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                }
            }
            TestContext.WriteLine($"ExplicitRemoteDelete: originalMembers={children + 1}; lostConflictCommit={loseConflictCommit}; boundedPage={pageTurn}; newRoleReadOnlyReconciliation={rotateRole}; lostOuterReceipt={loseCommandReceipt}; " +
                "defaultPollPreservation=True; originalConflictAndPollOutcomeUnchanged=True; explicitKeepRemote=True; sdkOwnsDeletion=True; " +
                "actualNativeAbsenceAndOfficialTombstones=True; separateCommandReceipt=True; noPredelete=True; noHydration=True; defaultHostIntegrated=False.");
        }
        finally
        {
            try
            {
                foreach (string[] page in files.Chunk(32))
                    await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, [], page);
                await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", "Docs/retire", "Docs/after"] : ["Docs", "Docs/after"], []);
            }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
