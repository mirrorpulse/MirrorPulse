using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCfSharpOriginalFileDeleteReplayCompletesItsOwnCommittedTombstoneAfterProgressLoss() =>
        RunCfSharpDeleteReplayAsync(directoryItem: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCfSharpOriginalDirectoryDeleteReplayCompletesItsOwnCommittedTombstoneAfterProgressLoss() =>
        RunCfSharpDeleteReplayAsync(directoryItem: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCfSharpPreservedCleanFileDeleteRetainsItsConflictAcrossOwners() =>
        RunCfSharpDeleteReplayAsync(directoryItem: false, preserveOnly: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCfSharpPreservedEmptyDirectoryDeleteRetainsItsConflictAcrossOwners() =>
        RunCfSharpDeleteReplayAsync(directoryItem: true, preserveOnly: true);

    private async Task RunCfSharpDeleteReplayAsync(bool directoryItem, bool preserveOnly = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.sdk-delete-replay"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        var identity = router.CreateFileIdentity(root.InstanceId, "docs", "sdk-only-delete", "v1");
        var kind = directoryItem ? CloudItemKind.Directory : CloudItemKind.File;
        var metadata = directoryItem ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        var birth = new CloudRemoteChangeBatch(root.InstanceId + "/sdk-birth", ReadOnlyMemory<byte>.Empty,
            [new("birth", directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                identity.RemoteId, "v1", kind, "Docs/retire", identity.ItemId, length: directoryItem ? null : 4,
                metadata: metadata, cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var original = new CloudRemoteChangeBatch(root.InstanceId + "/sdk-delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, identity.RemoteId, "v2", kind, "Docs/retire", identity.ItemId,
                previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        byte[] fingerprint = original.Fingerprint.ToArray();
        var fault = new MirrorPulseRemoteProgressCommitFault(original);
        Guid? preservedConflict = null;
        // Preview.7 retains even clean objects under preservation. This isolated SDK
        // recovery fixture explicitly authorizes deletion of its disposable original.
        var options = new CloudRemoteApplyOptions { MaximumEntries = 1, PreserveUnsynchronizedLocalContent = false };
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = CloudFileSystem.CreateBuilder(paths.SyncRootPath)
                .WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, caller.User!);
                SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
            }
            // No product catalog, actor, namespace lease, driver or Adapter cursor participates.
            // The state session only exposes the official provider's public transaction API.
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var sdk = CloudFileSystem.CreateBuilder(paths.SyncRootPath)
                    .WithStateStore(owner == 0 && !preserveOnly ? fault.Wrap(state) : state).WithContentProvider(provider).Build();
                await sdk.StartAsync(deadline.Token);
                if (preserveOnly)
                {
                    var preservingOptions = options with { PreserveUnsynchronizedLocalContent = true };
                    if (owner == 0) Assert.IsEmpty((await sdk.ApplyRemoteChangesAsync(birth, preservingOptions, deadline.Token)).ConflictIds);
                    var retained = await sdk.ApplyRemoteChangesAsync(original, preservingOptions, deadline.Token);
                    var records = await sdk.ListRemoteConflictsAsync(deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, retained.Status); Assert.IsFalse(retained.RequiresRetry);
                    Assert.AreEqual(1, retained.AppliedEntryCount);
                    CollectionAssert.AreEqual(original.FinalCursor.ToArray(), retained.SafeCursor.ToArray());
                    Assert.HasCount(1, records);
                    Assert.AreEqual(CloudRemoteConflictReason.Delete, records[0].Conflict.Reason);
                    if (owner == 0)
                    {
                        Assert.AreEqual(CloudRemoteApplyEntryStatus.Conflict, retained.Entries.Single().Status);
                        preservedConflict = records[0].ConflictId;
                    }
                    else Assert.AreEqual(preservedConflict, records[0].ConflictId);
                    CloudItem preserved = directoryItem ? sdk.GetDirectory("Docs/retire") : sdk.GetFile("Docs/retire");
                    Assert.IsTrue((await preserved.InspectAsync(deadline.Token)).Exists);
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    var item = (await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!;
                    Assert.IsFalse(item.IsTombstone); Assert.AreEqual("v1", item.RemoteRevision);
                    await transaction.RollbackAsync(deadline.Token);
                    Assert.AreEqual(0, fault.Failures); Assert.AreEqual(0, provider.Reads);
                    Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath));
                    TestContext.WriteLine($"CfSharpPreservedDelete: owner={owner}; directory={directoryItem}; objectRetained=True; durableConflicts=1; sourceReads=0; sdkPackage={MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion}.");
                    continue;
                }
                if (owner == 0)
                {
                    var created = await sdk.ApplyRemoteChangesAsync(birth, options, deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, created.Status);
                    Assert.IsEmpty(created.ConflictIds); Assert.IsFalse(created.RequiresRetry);
                    await Assert.ThrowsExactlyAsync<IOException>(() => sdk.ApplyRemoteChangesAsync(original, options, deadline.Token).AsTask());
                    Assert.AreEqual(1, fault.Failures);
                    Assert.AreEqual(1, fault.UncommittedProgress!.AppliedEntryCount);
                }
                await using (var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                {
                    var item = (await transaction.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))!;
                    var progress = (await transaction.RemoteBatches.GetAsync(original.BatchId, deadline.Token))!;
                    TestContext.WriteLine($"CfSharpDeleteReplayBefore: owner={owner}; directory={directoryItem}; " +
                        $"itemTombstone={item.IsTombstone}; itemRevision={item.RemoteRevision}; appliedEntryCount={progress.AppliedEntryCount}; " +
                        $"batchStatus={progress.Status}; fingerprint={Convert.ToHexString(progress.Fingerprint.Span)}; " +
                        $"safeCursor={Convert.ToHexString(progress.Cursor.Span)}.");
                    Assert.IsTrue(item.IsTombstone); Assert.AreEqual("v2", item.RemoteRevision);
                    Assert.AreEqual(0, progress.AppliedEntryCount);
                    CollectionAssert.AreEqual(fingerprint, progress.Fingerprint.ToArray());
                    CollectionAssert.AreEqual(original.InitialCursor.ToArray(), progress.Cursor.ToArray());
                    await transaction.RollbackAsync(deadline.Token);
                }
                CloudItem reference = directoryItem ? sdk.GetDirectory("Docs/retire") : sdk.GetFile("Docs/retire");
                Assert.IsFalse((await reference.InspectAsync(deadline.Token)).Exists);
                if (owner == 0) continue;

                var replay = await sdk.ApplyRemoteChangesAsync(original, options, deadline.Token);
                var conflicts = await sdk.ListRemoteConflictsAsync(deadline.Token);
                TestContext.WriteLine($"CfSharpDeleteReplayActual: directory={directoryItem}; status={replay.Status}; " +
                    $"requiresRetry={replay.RequiresRetry}; appliedEntryCount={replay.AppliedEntryCount}; " +
                    $"safeCursor={Convert.ToHexString(replay.SafeCursor.Span)}; entryStatuses={string.Join(',', replay.Entries.Select(entry => entry.Status))}; " +
                    $"conflicts={replay.ConflictIds.Count}; sdkPackage={MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion}; destructiveAuthorization=True; originalFingerprint={Convert.ToHexString(original.Fingerprint.Span)}; " +
                    $"sourceReads={provider.Reads}; productCatalogCreated={File.Exists(paths.ProductCatalogDatabasePath)}.");
                foreach (var record in conflicts)
                    TestContext.WriteLine($"CfSharpDeleteReplayConflict: reason={record.Conflict.Reason}; change={record.Conflict.Change.ChangeId}; " +
                        $"previousRevision={record.Conflict.Change.PreviousRemoteRevision}; requestedRevision={record.Conflict.Change.RemoteRevision}; " +
                        $"localRevision={record.Conflict.LocalState?.RemoteRevision}; localTombstone={record.Conflict.LocalState?.IsTombstone}.");
                Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath)); Assert.AreEqual(0, provider.Reads);
                CollectionAssert.AreEqual(fingerprint, original.Fingerprint.ToArray());
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, replay.Status); Assert.IsFalse(replay.RequiresRetry);
                Assert.AreEqual(1, replay.AppliedEntryCount);
                CollectionAssert.AreEqual(original.FinalCursor.ToArray(), replay.SafeCursor.ToArray());
                Assert.IsEmpty(replay.ConflictIds, "The unchanged delete must recover its own already committed tombstone.");
                Assert.IsEmpty(conflicts);
                Assert.HasCount(1, replay.Entries);
                Assert.IsTrue(replay.Entries[0].Status is CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied);
            }
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", "Docs/retire"] : ["Docs"], directoryItem ? [] : ["Docs/retire"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
