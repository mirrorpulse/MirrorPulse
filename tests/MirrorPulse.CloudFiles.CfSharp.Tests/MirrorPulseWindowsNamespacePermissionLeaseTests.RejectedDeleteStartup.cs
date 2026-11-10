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
    [DataRow(false, false, DisplayName = "ReceivedMissingDeleteBeforeCommitActive")]
    [DataRow(false, true, DisplayName = "ReceivedMissingDeleteBeforeCommitDisabled")]
    [DataRow(true, false, DisplayName = "ReceivedMissingDeleteAfterCommitActive")]
    [DataRow(true, true, DisplayName = "ReceivedMissingDeleteAfterCommitDisabled")]
    public Task NativeDefaultRuntimeRecoversReceivedMissingDeleteBeforeNewRoleAuditWithoutAcknowledgingLocalContentOriginals(bool afterSdkCommit, bool disable) =>
        RunReceivedMissingDeleteAcrossRolesAsync(afterSdkCommit, disable, interrupt: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeDefaultRuntimePublishesActualMissingDeleteConflictAndCursorAcrossFreshRolesWithoutChangingLocalOriginals() =>
        RunReceivedMissingDeleteAcrossRolesAsync(afterSdkCommit: false, disable: false, interrupt: false);

    private async Task RunReceivedMissingDeleteAcrossRolesAsync(bool afterSdkCommit, bool disable, bool interrupt)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rejected-delete-startup"), InstanceId.New(),
            new("docs", "Existing", "Existing", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var identity = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "missing-original", "v1").ToCfSharp();
        var original = new CloudRemoteChangeBatch(root.InstanceId + "/received-before-stop", ReadOnlyMemory<byte>.Empty,
            [new("original-rejected-delete", CloudRemoteChangeKind.Delete, identity.RemoteId, "v2", CloudItemKind.File,
                "Existing/missing.bin", identity.ItemId, cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var fault = new MirrorPulseRemoteRejectionCommitFault(original, afterSdkCommit);
        Guid creation = Guid.NewGuid(), localMember = Guid.Empty; string? firstRole = null;
        byte[] latest = "local originals must survive a received SDK rejection and a disabled new-role startup"u8.ToArray();
        Dictionary<Guid, string> originals = null!; byte[] retainedHeader = [];
        Dictionary<Guid, (CloudOperationJournalEntry Entry, string Path)> metadata = null!;
        MirrorPulseRemoteDeleteRejectionAdmission admission = null!;
        int observedOwners = 0;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(deadline.Token); SetProtectedFixtureUserOwner(setup.Root, caller.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                var current = owner == 1 && disable ? new RootRegistration(root.AdapterId, root.InstanceId, root.RootId,
                    root.UniquenessKey, root.Label, root.DirectoryName, root.CustomEntry, RootRegistrationState.Disabled,
                    root.RegisteredAt, root.IdentityScope) : root;
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, [current]);
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                if (owner == 0) firstRole = execution.RoleSid.Value; else Assert.AreNotEqual(firstRole, execution.RoleSid.Value);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                int publishedNotifications = 0;
                var center = new MirrorPulseConflictCenter();
                var notifications = new MirrorPulseConflictNotificationBridge((_, _) =>
                {
                    Interlocked.Increment(ref publishedNotifications); return ValueTask.CompletedTask;
                });
                DisabledCreationStoreObserver? state = null;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work),
                    uploads: source, stats: source, mutations: source, mayDispatch: _ => false,
                    router: router, catalog: catalog, namespaceExecution: execution, namespaceActor: actor,
                    scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true)
                {
                    StateStoreDecorator = inner =>
                    {
                        state = new(inner); return owner == 0 && interrupt ? fault.Wrap(state) : state;
                    }
                };
                await using var runtime = factory.Create(paths); await runtime.StartAsync(deadline.Token);
                Assert.IsNotNull(state);
                var parent = await runtime.InspectNamespaceMemberAsync("Existing", true, deadline.Token);
                if (owner == 0)
                {
                    var created = await runtime.CreateNamespaceMemberAsync(creation, parent.MemberId, "local.txt", false, deadline.Token);
                    localMember = created.MemberId;
                    await using (var writer = new FileStream(Path.Combine(paths.SyncRootPath, "Existing", "local.txt"), FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(latest, deadline.Token); writer.SetLength(latest.Length); writer.Flush(true);
                    }
                    var local = await runtime.InspectNamespaceMemberAsync("Existing/local.txt", false, deadline.Token);
                    bool diagnosticWritten = false;
                    while (true)
                    {
                        await using var read = await state.Store.BeginTransactionAsync(deadline.Token);
                        var rows = await read.Operations.ListAsync(64, deadline.Token);
                        if (!diagnosticWritten)
                        {
                            var facts = new List<object>();
                            foreach (var row in rows)
                            {
                                var item = row.ItemId is { } itemId ? await read.Items.GetByItemIdAsync(itemId, deadline.Token) : null;
                                facts.Add(new { row.OperationId, row.Kind, row.ItemId, relativePath = item?.RelativePath });
                            }
                            TestContext.WriteLine("Runtime original lookup: " + JsonSerializer.Serialize(new { local.NativeItemId, facts }));
                            diagnosticWritten = true;
                        }
                        originals = rows.Where(value => value.Kind != CloudStateOperationKind.MetadataUpdate)
                            .ToDictionary(value => value.OperationId, DisabledCreationJournalFact);
                        metadata = [];
                        foreach (var row in rows.Where(value => value.Kind == CloudStateOperationKind.MetadataUpdate))
                        {
                            var item = row.ItemId is { } itemId ? await read.Items.GetByItemIdAsync(itemId, deadline.Token) : null;
                            Assert.IsNotNull(item); metadata.Add(row.OperationId, (row, item.RelativePath.Replace('\\', '/')));
                        }
                        bool found = rows.Any(value => value.ItemId == local.NativeItemId && value.Kind == CloudStateOperationKind.Create) &&
                            rows.Any(value => value.ItemId == local.NativeItemId && value.Kind == CloudStateOperationKind.ContentUpdate);
                        await read.RollbackAsync(deadline.Token);
                        if (found) break;
                        await Task.Delay(25, deadline.Token);
                    }
                    try
                    {
                        var applied = await runtime.ApplyRemoteBatchAsync(root.InstanceId, original, catalog, center, notifications, null, deadline.Token);
                        if (interrupt) Assert.IsTrue(applied.RequiresRetry);
                        else
                        {
                            Assert.AreEqual(CloudRemoteBatchStatus.Applied, applied.Status); Assert.IsFalse(applied.RequiresRetry);
                            CollectionAssert.AreEqual(original.FinalCursor.ToArray(), applied.SafeCursor.ToArray());
                        }
                    }
                    catch (IOException) when (interrupt) { }
                    Assert.AreEqual(interrupt ? 1 : 0, fault.Failures);
                    var pending = (await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, 0, deadline.Token))!;
                    Assert.IsNotNull(pending); admission = pending.Admission;
                    if (interrupt)
                    {
                        Assert.IsNull(pending.Outcome);
                        retainedHeader = (await catalog.ReadRetainedRemoteBatchAsync(root.InstanceId, deadline.Token))!.Payload;
                        Assert.Contains(root.RootId, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                    }
                    else
                    {
                        Assert.IsNotNull(pending.Outcome);
                        Assert.IsNull(await catalog.ReadRetainedRemoteBatchAsync(root.InstanceId, deadline.Token));
                        var projected = center.Query(root.InstanceId).Single();
                        Assert.AreEqual(pending.Outcome.ConflictId, projected.ConflictId);
                        Assert.AreEqual(original.Changes[0].ChangeId, projected.ChangeId);
                        Assert.AreEqual(original.Changes[0].RelativePath, projected.RelativePath);
                        Assert.IsGreaterThan(0, publishedNotifications);
                    }
                    await using var progress = await state.Store.BeginTransactionAsync(deadline.Token);
                    Assert.AreEqual(afterSdkCommit || !interrupt ? 1 : 0,
                        (await progress.RemoteBatches.GetAsync(original.BatchId, deadline.Token))?.AppliedEntryCount ?? 0);
                    await progress.RollbackAsync(deadline.Token);
                }
                else
                {
                    var recovered = (await catalog.ReadRemoteDeleteRejectionAsync(root.InstanceId, original.BatchId, 0, deadline.Token))!;
                    Assert.AreEqual(admission, recovered.Admission); Assert.IsNotNull(recovered.Outcome);
                    Assert.IsEmpty(await catalog.ReadPendingRemoteDeleteRejectionsAsync(root.RootId, token: deadline.Token));
                    Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, deadline.Token));
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    var protection = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent.MemberId, deadline.Token))!;
                    Assert.AreEqual(execution.RoleSid.Value, protection.Context.Protection.RoleSid);
                    Assert.AreEqual(localMember, (await runtime.InspectNamespaceMemberAsync("Existing/local.txt", false, deadline.Token)).MemberId);
                    CollectionAssert.AreEqual(latest, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Existing", "local.txt"), deadline.Token));
                    var incoming = await catalog.ReadRetainedRemoteBatchAsync(root.InstanceId, deadline.Token);
                    if (interrupt) { Assert.IsNotNull(incoming); CollectionAssert.AreEqual(retainedHeader, incoming.Payload); }
                    else Assert.IsNull(incoming);
                    var unsupported = (await catalog.ReadBlockedLocalOperationsAsync(deadline.Token)).ToDictionary(value => value.OperationId);
                    await using var read = await state.Store.BeginTransactionAsync(deadline.Token);
                    foreach (var retained in originals)
                    {
                        var actual = await read.Operations.GetAsync(retained.Key, deadline.Token);
                        Assert.IsNotNull(actual, "The captured original is missing: " + retained.Value);
                        Assert.AreEqual(retained.Value, DisabledCreationJournalFact(actual));
                        Assert.IsFalse(unsupported.ContainsKey(retained.Key));
                    }
                    foreach (var retained in metadata.Values)
                    {
                        var actual = await read.Operations.GetAsync(retained.Entry.OperationId, deadline.Token);
                        if (actual is not null) Assert.AreEqual(DisabledCreationJournalFact(retained.Entry), DisabledCreationJournalFact(actual));
                        else
                        {
                            Assert.IsTrue(unsupported.TryGetValue(retained.Entry.OperationId, out var result));
                            Assert.AreEqual(root.InstanceId, result.InstanceId);
                            Assert.AreEqual(retained.Path, result.RelativePath.Replace('\\', '/'));
                            Assert.AreEqual(MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, result.Reason);
                            Assert.AreEqual(retained.Entry.CreatedAt, result.ObservedAt);
                        }
                    }
                    Assert.IsNotNull(await read.Conflicts.GetAsync(recovered.Outcome.ConflictId, deadline.Token));
                    var cursor = await read.Checkpoints.GetAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(root.InstanceId), deadline.Token);
                    if (interrupt) Assert.IsNull(cursor);
                    else { Assert.IsNotNull(cursor); CollectionAssert.AreEqual(original.FinalCursor.ToArray(), cursor.Value.ToArray()); }
                    await read.RollbackAsync(deadline.Token);
                    if (disable)
                    {
                        Assert.IsFalse(work.IsAdmitted(root.RootId));
                        _ = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), parent.MemberId, "later.txt", false, deadline.Token);
                    }
                }
                Assert.IsEmpty(worker.Calls); observedOwners++;
            }
            Assert.AreEqual(2, observedOwners);
            TestContext.WriteLine($"ReceivedDeleteRecovery: afterActualSdkCommit={afterSdkCommit}; disabled={disable}; interruption={interrupt}; freshRole=True; " +
                $"actualRetainedHeader={interrupt}; originalLocalCreateAndContentJournalAndLatestBytes=True; metadataRetainedOrDurablyUnsupported=True; " +
                $"sourceCalls=0; productCursorPublished={!interrupt}; actualConflictProjected={!interrupt}; nativeRetirementInvented=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }
}
