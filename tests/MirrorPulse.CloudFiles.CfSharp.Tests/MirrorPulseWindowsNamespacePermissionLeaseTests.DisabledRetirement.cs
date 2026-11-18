using System.Security.Principal;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeDisabledRootControlledRetirementRetainsOriginalJournalAcrossFreshRolesWithoutDispatch() =>
        VerifyDisabledRootControlledRetirementAcrossFreshRolesAsync(null);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("BeforeNativeDeletion")]
    [DataRow("AfterNativeDeletion")]
    [DataRow("AfterNativeReceipt")]
    public Task NativeInterruptedControlledSubtreeRetirementUsesOriginalLeafAcrossHostRoles(string checkpoint) =>
        VerifyDisabledRootControlledRetirementAcrossFreshRolesAsync(Enum.Parse<MirrorPulseNamespaceRetirementCheckpoint>(checkpoint));

    private async Task VerifyDisabledRootControlledRetirementAcrossFreshRolesAsync(MirrorPulseNamespaceRetirementCheckpoint? interruption)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.disabled-retirement"), InstanceId.New(),
            new("existing", "Existing", "Existing", false), RootRegistrationState.Disabled,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid fileBirth = Guid.NewGuid(), directoryBirth = Guid.NewGuid(), fileRetirement = Guid.NewGuid(), directoryRetirement = Guid.NewGuid();
        Guid subtreeBirth = Guid.NewGuid(), nestedBirth = Guid.NewGuid(), leafBirth = Guid.NewGuid(), batchOperationId = Guid.NewGuid();
        MirrorPulseNamespaceMemberRegistration? firstFile = null, firstDirectory = null;
        MirrorPulseNamespaceRetirementBatch? completedBatch = null, preparedBeforeRestart = null;
        MirrorPulseNamespaceRetirement? retainedBeforeRestart = null;
        string? interruptedDeleteFact = null;
        Guid restartOperationId = Guid.NewGuid();
        Dictionary<Guid, Guid>? batchNativeItems = null;
        string? originalRole = null, birthHistory = null, retirementHistory = null;
        Guid? fileItemId = null, directoryItemId = null;
        Dictionary<Guid, string>? originals = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token); SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                if (owner == 0) originalRole = execution.RoleSid.Value;
                else Assert.AreNotEqual(originalRole, execution.RoleSid.Value);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                DisabledCreationStoreObserver? state = null;
                bool interruptRetirement = false;
                bool countRecoveryDeletes = false;
                int recoveryNativeDeletes = 0;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work),
                    uploads: source, stats: source, mutations: source, mayDispatch: _ => false,
                    router: router, catalog: catalog, namespaceExecution: execution, namespaceActor: actor,
                    scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true)
                {
                    StateStoreDecorator = inner => state = new(inner),
                    ControlledRetirementCheckpoint = checkpoint =>
                    {
                        if (interruptRetirement && checkpoint == interruption)
                            return ValueTask.FromException(new IOException("Interrupted the original leaf retirement."));
                        if (countRecoveryDeletes && checkpoint == MirrorPulseNamespaceRetirementCheckpoint.AfterNativeDeletion)
                            recoveryNativeDeletes++;
                        return ValueTask.CompletedTask;
                    }
                };
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                var parent = await runtime.InspectNamespaceMemberAsync("Existing", true, timeout.Token);
                var file = await runtime.CreateNamespaceMemberAsync(fileBirth, parent.MemberId, "delete.txt", false, timeout.Token);
                var folder = await runtime.CreateNamespaceMemberAsync(directoryBirth, parent.MemberId, "delete-folder", true, timeout.Token);
                var subtree = await runtime.CreateNamespaceMemberAsync(subtreeBirth, parent.MemberId, "delete-tree", true, timeout.Token);
                var nested = await runtime.CreateNamespaceMemberAsync(nestedBirth, subtree.MemberId, "nested", true, timeout.Token);
                var leaf = await runtime.CreateNamespaceMemberAsync(leafBirth, nested.MemberId, "leaf.bin", false, timeout.Token);
                Assert.IsNotNull(state);
                if (owner == 0)
                {
                    firstFile = file; firstDirectory = folder;
                    fileItemId = (await runtime.InspectNamespaceMemberAsync("Existing/delete.txt", false, timeout.Token)).NativeItemId;
                    directoryItemId = (await runtime.InspectNamespaceMemberAsync("Existing/delete-folder", true, timeout.Token)).NativeItemId;
                    Assert.IsNotNull(fileItemId); Assert.IsNotNull(directoryItemId);
                    batchNativeItems = new();
                    foreach (var member in new[] { subtree, nested, leaf })
                    {
                        var observed = await runtime.InspectNamespaceMemberAsync(member.RelativePath, member.IsDirectory, timeout.Token);
                        Assert.IsNotNull(observed.NativeItemId);
                        batchNativeItems.Add(member.MemberId, observed.NativeItemId.Value);
                    }
                    byte[] bytes = "explicit offline deletion retains its original pending local history"u8.ToArray();
                    await using (var writer = new FileStream(Path.Combine(paths.SyncRootPath, "Existing", "delete.txt"), FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(bytes, timeout.Token); writer.SetLength(bytes.Length); writer.Flush(true);
                    }
                    birthHistory = JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileBirth, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryBirth, timeout.Token) });
                    await using var read = await state.Store.BeginTransactionAsync(timeout.Token);
                    originals = (await read.Operations.ListAsync(64, timeout.Token)).ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                    Assert.IsGreaterThan(0, originals.Count); await read.RollbackAsync(timeout.Token);
                }
                else
                {
                    Assert.AreEqual(firstFile, file); Assert.AreEqual(firstDirectory, folder);
                    Assert.AreEqual(birthHistory, JsonSerializer.Serialize(new[] { await catalog.ReadNamespaceBirthAsync(fileBirth, timeout.Token),
                        await catalog.ReadNamespaceBirthAsync(directoryBirth, timeout.Token) }));
                }
                _ = await runtime.PrepareNamespaceMemberRetirementAsync(fileRetirement, file.MemberId, timeout.Token);
                var retiredFile = await runtime.ExecuteNamespaceMemberRetirementAsync(fileRetirement, timeout.Token);
                _ = await runtime.PrepareNamespaceMemberRetirementAsync(directoryRetirement, folder.MemberId, timeout.Token);
                var retiredDirectory = await runtime.ExecuteNamespaceMemberRetirementAsync(directoryRetirement, timeout.Token);
                Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, retiredFile.Phase);
                Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, retiredDirectory.Phase);
                string completed = JsonSerializer.Serialize(new[] { retiredFile, retiredDirectory });
                if (owner == 0) retirementHistory = completed;
                else Assert.AreEqual(retirementHistory, completed);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceMemberRetirementAsync(
                    fileRetirement, folder.MemberId, timeout.Token).AsTask());
                var preparedBatch = await runtime.PrepareNamespaceRetirementBatchAsync(batchOperationId, subtree.MemberId, timeout.Token);
                Assert.HasCount(3, preparedBatch.Entries);
                Assert.AreEqual(owner == 0 ? MirrorPulseNamespaceRetirementBatchPhase.Prepared : MirrorPulseNamespaceRetirementBatchPhase.Completed,
                    preparedBatch.Phase);
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                if (owner == 0)
                {
                    CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray(), root.RootId);
                    Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, leaf.RelativePath)));
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceRetirementBatchAsync(
                        Guid.NewGuid(), nested.MemberId, timeout.Token).AsTask());
                }
                var retiredBatch = await runtime.ExecuteNamespaceRetirementBatchAsync(batchOperationId, timeout.Token);
                Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, retiredBatch.Phase);
                Assert.AreEqual(batchOperationId, retiredBatch.OperationId);
                Assert.AreEqual(subtree.MemberId, retiredBatch.MemberId);
                CollectionAssert.AreEqual(preparedBatch.Entries.ToArray(), retiredBatch.Entries.ToArray());
                if (owner == 0) completedBatch = retiredBatch;
                else Assert.AreEqual(JsonSerializer.Serialize(completedBatch), JsonSerializer.Serialize(retiredBatch));
                foreach (var member in new[] { leaf, nested, subtree })
                {
                    var receipt = await catalog.ReadNamespaceRetirementAsync(retiredBatch.MemberOperationId(member.MemberId), timeout.Token);
                    Assert.IsNotNull(receipt);
                    Assert.AreEqual(batchOperationId, receipt.Intent.BatchOperationId);
                    Assert.AreEqual(batchNativeItems![member.MemberId], receipt.StartedItemId);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, receipt.Phase);
                }
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareNamespaceRetirementBatchAsync(
                    batchOperationId, folder.MemberId, timeout.Token).AsTask());
                Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, subtree.RelativePath)));
                Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Existing", "delete.txt")));
                Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Existing", "delete-folder")));
                Assert.IsFalse(work.IsAdmitted(root.RootId)); Assert.IsFalse(work.TryEnter([root.RootId], out var ordinary)); Assert.IsNull(ordinary);
                if (owner == 0)
                {
                    Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                }
                await using (var read = await state.Store.BeginTransactionAsync(timeout.Token))
                {
                    foreach (var original in originals!)
                    {
                        var retained = await read.Operations.GetAsync(original.Key, timeout.Token);
                        Assert.IsNotNull(retained); Assert.AreEqual(original.Value, DisabledCreationJournalFact(retained));
                    }
                    await read.RollbackAsync(timeout.Token);
                }
                var expectedDeletes = batchNativeItems!.Values.Append(fileItemId!.Value).Append(directoryItemId!.Value).ToArray();
                var pending = await ReadDisabledRetirementJournalAsync(state.Store, expectedDeletes, timeout.Token);
                var deletes = pending.Where(row => row.Kind == CloudStateOperationKind.Delete).OrderBy(row => row.Sequence).ToArray();
                if (owner == 1 && interruption is (MirrorPulseNamespaceRetirementCheckpoint.AfterNativeDeletion or
                    MirrorPulseNamespaceRetirementCheckpoint.AfterNativeReceipt))
                {
                    // The native cut can precede observation by the SDK's journal consumer.
                    // Preserve any visible original now and require exactly one after recovery.
                    var interruptedDeletes = deletes.Where(row => row.ItemId == retainedBeforeRestart!.StartedItemId).ToArray();
                    Assert.IsLessThanOrEqualTo(1, interruptedDeletes.Length);
                    interruptedDeleteFact = interruptedDeletes.Select(DisabledCreationJournalFact).SingleOrDefault();
                    deletes = deletes.Where(row => row.ItemId != retainedBeforeRestart!.StartedItemId).ToArray();
                }
                Assert.HasCount(5, deletes);
                CollectionAssert.AreEqual(new[] { batchNativeItems[leaf.MemberId], batchNativeItems[nested.MemberId], batchNativeItems[subtree.MemberId] },
                    deletes.Where(row => batchNativeItems.ContainsValue(row.ItemId!.Value)).Select(row => row.ItemId!.Value).ToArray());
                Assert.AreEqual(1, pending.Count(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == fileItemId));
                Assert.AreEqual(1, pending.Count(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == directoryItemId));
                if (owner == 0) originals = pending.ToDictionary(row => row.OperationId, DisabledCreationJournalFact);
                if (owner == 0)
                {
                    var pendingTree = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), parent.MemberId, "retire-after-restart", true, timeout.Token);
                    var pendingLeaf = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), pendingTree.MemberId, "original.bin", false, timeout.Token);
                    await using (var writer = new FileStream(Path.Combine(paths.SyncRootPath, pendingLeaf.RelativePath),
                        FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(new byte[] { 7, 8, 9 }, timeout.Token);
                        writer.Flush(true);
                    }
                    preparedBeforeRestart = await runtime.PrepareNamespaceRetirementBatchAsync(restartOperationId, pendingTree.MemberId, timeout.Token);
                    Assert.HasCount(2, preparedBeforeRestart.Entries);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Prepared, preparedBeforeRestart.Phase);
                    Guid leafOperation = preparedBeforeRestart.MemberOperationId(pendingLeaf.MemberId);
                    Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(leafOperation, timeout.Token));
                    if (interruption is not null)
                    {
                        var native = await runtime.InspectNamespaceMemberAsync(pendingLeaf.RelativePath, false, timeout.Token);
                        interruptRetirement = true;
                        var error = await Assert.ThrowsExactlyAsync<IOException>(() => runtime.ExecuteNamespaceRetirementBatchAsync(
                            restartOperationId, timeout.Token).AsTask());
                        Assert.AreEqual("Interrupted the original leaf retirement.", error.Message);
                        retainedBeforeRestart = await catalog.ReadNamespaceRetirementAsync(leafOperation, timeout.Token);
                        Assert.IsNotNull(retainedBeforeRestart);
                        Assert.IsNotNull(retainedBeforeRestart.StartedAt);
                        Assert.IsNotNull(native.NativeItemId);
                        Assert.AreEqual(native.NativeItemId, retainedBeforeRestart.StartedItemId);
                        Assert.AreEqual(execution.RoleSid.Value, retainedBeforeRestart.Intent.RoleSid);
                        Assert.AreEqual(interruption == MirrorPulseNamespaceRetirementCheckpoint.AfterNativeReceipt
                            ? MirrorPulseNamespaceRetirementPhase.NativeDeleted : MirrorPulseNamespaceRetirementPhase.Prepared, retainedBeforeRestart.Phase);
                    }
                }
                else
                {
                    Assert.IsNotNull(preparedBeforeRestart);
                    var startup = runtime.NamespaceStartupResults.Single(result => result.RootId == root.RootId);
                    Assert.IsFalse(startup.Admitted);
                    Assert.AreEqual(restartOperationId, startup.OperationId);
                    var retained = await runtime.ReadNamespaceRetirementBatchAsync(restartOperationId, timeout.Token);
                    Assert.AreEqual(JsonSerializer.Serialize(preparedBeforeRestart), JsonSerializer.Serialize(retained));
                    var originalLeaf = retained.Entries.Single(entry => !entry.IsDirectory);
                    var originalLeafPath = Path.Combine(paths.SyncRootPath, originalLeaf.RelativePath);
                    if (interruption is null or MirrorPulseNamespaceRetirementCheckpoint.BeforeNativeDeletion)
                        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(originalLeafPath, timeout.Token));
                    else Assert.IsFalse(File.Exists(originalLeafPath));
                    Assert.AreEqual(retainedBeforeRestart, await catalog.ReadNamespaceRetirementAsync(retained.MemberOperationId(originalLeaf.MemberId), timeout.Token));
                    countRecoveryDeletes = true;
                    var resumed = await runtime.ExecuteNamespaceRetirementBatchAsync(restartOperationId, timeout.Token);
                    Assert.AreEqual(interruption is null or MirrorPulseNamespaceRetirementCheckpoint.BeforeNativeDeletion ? 2 : 1, recoveryNativeDeletes);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, resumed.Phase);
                    CollectionAssert.AreEqual(preparedBeforeRestart.Entries.ToArray(), resumed.Entries.ToArray());
                    Assert.AreEqual(restartOperationId, resumed.OperationId);
                    foreach (var entry in preparedBeforeRestart.Entries)
                    {
                        var receipt = await catalog.ReadNamespaceRetirementAsync(resumed.MemberOperationId(entry.MemberId), timeout.Token);
                        Assert.IsNotNull(receipt);
                        Assert.AreEqual(entry.LocalObject, receipt.Intent.LocalObject);
                        if (retainedBeforeRestart is not null && entry.MemberId == originalLeaf.MemberId)
                        {
                            Assert.AreEqual(retainedBeforeRestart.Intent, receipt.Intent);
                            Assert.AreEqual(retainedBeforeRestart.StartedAt, receipt.StartedAt);
                            Assert.AreEqual(retainedBeforeRestart.StartedItemId, receipt.StartedItemId);
                            if (retainedBeforeRestart.NativeDeletedAt is { } committedDeletion)
                                Assert.AreEqual(committedDeletion, receipt.NativeDeletedAt);
                            var journal = await ReadDisabledRetirementJournalAsync(state.Store, [receipt.StartedItemId!.Value], timeout.Token);
                            var resumedDeletes = journal.Where(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == receipt.StartedItemId).ToArray();
                            Assert.HasCount(1, resumedDeletes, "The SDK must retain exactly one Delete for the original native item after batch recovery.");
                            if (interruptedDeleteFact is not null)
                                Assert.AreEqual(interruptedDeleteFact, DisabledCreationJournalFact(resumedDeletes[0]));
                        }
                        else Assert.AreEqual(execution.RoleSid.Value, receipt.Intent.RoleSid);
                        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, receipt.Phase);
                    }
                    Assert.IsFalse(File.Exists(originalLeafPath));
                    Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                }
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.IsEmpty(worker.Calls);
            }
            TestContext.WriteLine("DisabledRetirement: fileAndEmptyDirectoryDeleted=True; originalBirthAndJournalFactsRetained=True; " +
                "protectedSubtreeRetiredLeafFirst=True; originalBatchReceiptsRetained=True; " +
                "actualDisposedRoles=True; completedReplayDoesNotResurrect=True; preparedBatchRecoveredInNewRole=True; " +
                $"interruption={interruption?.ToString() ?? "BeforeFirstLeaf"}; ordinaryAdmissionClosed=True; sourceCalls=0; remoteAcceptance=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }

    private static async Task<IReadOnlyList<CloudOperationJournalEntry>> ReadDisabledRetirementJournalAsync(
        ICloudStateStore store, IReadOnlyList<Guid> expectedDeletes, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            await using var read = await store.BeginTransactionAsync(token);
            var journal = await read.Operations.ListAsync(64, token);
            await read.RollbackAsync(token);
            if (expectedDeletes.All(item => journal.Any(row => row.Kind == CloudStateOperationKind.Delete && row.ItemId == item)) ||
                DateTime.UtcNow >= deadline) return journal;
            await Task.Delay(25, token);
        }
    }
}
