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
    public Task NativeCfSharpNewFileIdentityAtRetiredPathDoesNotAdoptTheOriginalTombstone() =>
        RunCfSharpExplicitRecreationAsync(directoryItem: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeCfSharpNewDirectoryIdentityAtRetiredPathDoesNotAdoptTheOriginalTombstone() =>
        RunCfSharpExplicitRecreationAsync(directoryItem: true);

    private async Task RunCfSharpExplicitRecreationAsync(bool directoryItem)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.sdk-recreation"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        var previous = router.CreateFileIdentity(root.InstanceId, "docs", "sdk-original", "v1");
        var next = router.CreateFileIdentity(root.InstanceId, "docs", "sdk-successor", "v3");
        var kind = directoryItem ? CloudItemKind.Directory : CloudItemKind.File;
        var upsert = directoryItem ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert;
        var metadata = directoryItem ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        const string relativePath = "Docs/recreated";
        var birth = new CloudRemoteChangeBatch(root.InstanceId + "/original-birth", ReadOnlyMemory<byte>.Empty,
            [new("original-birth", upsert, previous.RemoteId, "v1", kind, relativePath, previous.ItemId,
                length: directoryItem ? null : 4, metadata: metadata, cursorAfter: new byte[] { 1 })], new byte[] { 1 });
        var deletion = new CloudRemoteChangeBatch(root.InstanceId + "/original-delete", new byte[] { 1 },
            [new("original-delete", CloudRemoteChangeKind.Delete, previous.RemoteId, "v2", kind, relativePath, previous.ItemId,
                previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        var recreation = new CloudRemoteChangeBatch(root.InstanceId + "/successor-birth", new byte[] { 2 },
            [new("successor-birth", upsert, next.RemoteId, "v3", kind, relativePath, next.ItemId,
                length: directoryItem ? null : 7, metadata: metadata, cursorAfter: new byte[] { 3 })], new byte[] { 3 });
        var options = new CloudRemoteApplyOptions { MaximumEntries = 1, PreserveUnsynchronizedLocalContent = false };
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var originalState = new MirrorPulseCfSharpStateSession(paths);
            await using (var sdk = CloudFileSystem.CreateBuilder(paths.SyncRootPath).WithStateStore(originalState).WithContentProvider(provider).Build())
            {
                await sdk.StartAsync(deadline.Token);
                (await sdk.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(sdk.Root, caller.User!);
                SetProtectedFixtureUserOwner(sdk.GetDirectory("Docs"), caller.User!);
                var created = await sdk.ApplyRemoteChangesAsync(birth, options, deadline.Token);
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, created.Status); Assert.IsEmpty(created.ConflictIds);
                var deleted = await sdk.ApplyRemoteChangesAsync(deletion, options, deadline.Token);
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, deleted.Status); Assert.IsEmpty(deleted.ConflictIds);
                CloudItem originalReference = directoryItem ? sdk.GetDirectory(relativePath) : sdk.GetFile(relativePath);
                Assert.IsFalse((await originalReference.InspectAsync(deadline.Token)).Exists);
                await using var transaction = await originalState.OpenStore.BeginTransactionAsync(deadline.Token);
                var tombstone = (await transaction.Items.GetByItemIdAsync(previous.ItemId, deadline.Token))!;
                Assert.IsTrue(tombstone.IsTombstone); Assert.AreEqual("v2", tombstone.RemoteRevision);
                await transaction.RollbackAsync(deadline.Token);
            }
            // Reopen the actual official store. No product catalog, generation resolver,
            // namespace actor, manual tombstone update, or Adapter cursor participates.
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using (var sdk = CloudFileSystem.CreateBuilder(paths.SyncRootPath).WithStateStore(state).WithContentProvider(provider).Build())
            {
                await sdk.StartAsync(deadline.Token);
                var result = await sdk.ApplyRemoteChangesAsync(recreation, options, deadline.Token);
                CloudItem reference = directoryItem ? sdk.GetDirectory(relativePath) : sdk.GetFile(relativePath);
                var snapshot = await reference.InspectAsync(deadline.Token);
                var actual = snapshot.IsPlaceholder ? CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span) : null;
                await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                var oldItem = await transaction.Items.GetByItemIdAsync(previous.ItemId, deadline.Token);
                var newItem = await transaction.Items.GetByItemIdAsync(next.ItemId, deadline.Token);
                var byPath = await transaction.Items.GetByRelativePathAsync(relativePath, deadline.Token);
                var originalProgress = await transaction.RemoteBatches.GetAsync(deletion.BatchId, deadline.Token);
                bool deleteFingerprintPreserved = originalProgress is not null && originalProgress.Fingerprint.Span.SequenceEqual(deletion.Fingerprint.Span);
                TestContext.WriteLine($"CfSharpExplicitRecreation: directory={directoryItem}; sdkPackage={MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion}; " +
                    $"status={result.Status}; requiresRetry={result.RequiresRetry}; appliedEntryCount={result.AppliedEntryCount}; conflicts={result.ConflictIds.Count}; " +
                    $"nativeExists={snapshot.Exists}; nativePlaceholder={snapshot.IsPlaceholder}; requestedNewIdentity={next.ItemId}; " +
                    $"actualNativeIdentity={actual?.ItemId}; oldItemTombstone={oldItem?.IsTombstone}; newItemExists={newItem is not null}; " +
                    $"pathItemIdentity={byPath?.ItemId}; originalDeleteFingerprintPreserved={deleteFingerprintPreserved}; " +
                    $"sourceReads={provider.Reads}; productCatalogCreated={File.Exists(paths.ProductCatalogDatabasePath)}.");
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status); Assert.IsFalse(result.RequiresRetry); Assert.IsEmpty(result.ConflictIds);
                CollectionAssert.AreEqual(recreation.FinalCursor.ToArray(), result.SafeCursor.ToArray());
                Assert.AreEqual(0, provider.Reads); Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath));
                Assert.AreEqual(next.ItemId, actual?.ItemId, "A new explicit identity at a retired path must not revive the original public item.");
                Assert.AreEqual(next.RemoteId, actual!.RemoteId); Assert.AreEqual("v3", actual.RemoteRevision);
                Assert.IsNotNull(oldItem); Assert.IsTrue(oldItem.IsTombstone); Assert.AreEqual(previous.RemoteId, oldItem.RemoteId);
                Assert.AreEqual("v2", oldItem.RemoteRevision); Assert.IsNotNull(newItem); Assert.IsFalse(newItem.IsTombstone);
                Assert.AreEqual(next.RemoteId, newItem.RemoteId); Assert.AreEqual(next.ItemId, byPath?.ItemId);
                CollectionAssert.AreEqual(deletion.Fingerprint.ToArray(), originalProgress!.Fingerprint.ToArray());
                await transaction.RollbackAsync(deadline.Token);
            }
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, directoryItem ? ["Docs", relativePath] : ["Docs"], directoryItem ? [] : [relativePath]); }
            finally { registry.Unregister(paths.SyncRootPath); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        }
    }
}
