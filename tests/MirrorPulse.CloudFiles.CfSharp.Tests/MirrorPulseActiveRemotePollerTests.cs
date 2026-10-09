using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task PollerUsesCurrentLabelAndAvailabilityWithoutChangingObjectIdentity()
    {
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.current-root"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Files",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), "MirrorPulse-current-root"), [root]);
        RootRegistration Renamed(RootRegistrationState state) => new(root.AdapterId, root.InstanceId, root.RootId,
            root.UniquenessKey, "Renamed", "Renamed", root.CustomEntry, state, root.RegisteredAt, root.IdentityScope);
        var source = new FakeDirectorySource();
        var batches = new List<CloudRemoteChangeBatch>();
        await using var poller = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
        {
            batches.Add(batch);
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }, currentRoots: () => router.Registrations);
        source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        router.ReplaceRegistrations([Renamed(RootRegistrationState.Active)]);
        Assert.IsFalse(await poller.PollOnceAsync(instance), "A display rename must not generate a source object move.");
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
        router.ReplaceRegistrations([Renamed(RootRegistrationState.Disabled)]);
        int reads = source.Reads;
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        Assert.AreEqual(reads, source.Reads, "A disabled root must not enumerate its source.");
        Assert.IsEmpty(batches);
        router.ReplaceRegistrations([Renamed(RootRegistrationState.Active)]);
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        CloudRemoteChange changed = batches.Single().Changes.Single();
        Assert.AreEqual("Renamed\\file.bin", changed.RelativePath);
        Assert.AreEqual(router.CreateFileIdentity(instance, "files", "file", "v2").ItemId, changed.ItemId);
        Assert.AreEqual(MirrorPulsePlaceholderIdentity.CreateForRoot(root, "file", "v1").ToCfSharp().ItemId, changed.ItemId);
        Assert.AreEqual(CloudRemoteChangeKind.FileUpsert, changed.Kind);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AddingRemoteSiblingDoesNotInvalidateUnchangedContent(bool metadataChanged)
    {
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.resident-poll"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Files",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var batches = new List<CloudRemoteChangeBatch>();
        await using var poller = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
        {
            batches.Add(batch);
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        });
        source.Set(new FakeEntry("resident", "v1", CloudItemKind.File, "resident.bin", 2097409));
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        source.Set(new FakeEntry("resident", "v1", CloudItemKind.File, "resident.bin", 2097409,
            metadataChanged ? DateTimeOffset.UnixEpoch.AddYears(55) : null),
            new FakeEntry("sibling", "v1", CloudItemKind.File, "sibling.txt", 1));
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        Assert.HasCount(1, batches.Single().Changes,
            "An unrelated remote addition must not turn unchanged resident bytes into a FileUpsert.");
        Assert.AreEqual("Files\\sibling.txt", batches.Single().Changes.Single().RelativePath);
        source.Set(new FakeEntry("resident", "v2", CloudItemKind.File, "resident.bin", 2097409),
            new FakeEntry("sibling", "v1", CloudItemKind.File, "sibling.txt", 1));
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        CloudRemoteChange changed = batches[1].Changes.Single();
        Assert.AreEqual(CloudRemoteChangeKind.FileUpsert, changed.Kind);
        Assert.AreEqual("v1", changed.PreviousRemoteRevision);
        Assert.AreEqual("v2", changed.RemoteRevision);
    }

    [TestMethod]
    public async Task LegacyPendingBatchKeepsItsOriginalFingerprintBeforeNewProjectionRuns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-poll-version-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.legacy-poll"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Files",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var pendingStore = new MirrorPulseCatalogRemotePollPendingStore(catalog);
            CloudRemoteChangeBatch? captured = null;
            await using (var first = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
            {
                captured = batch;
                throw new IOException("fixture retains the captured batch");
            }, snapshotStore: snapshots, pendingStore: pendingStore))
            {
                source.Set(new FakeEntry("resident", "v1", CloudItemKind.File, "resident.bin", 3));
                Assert.IsFalse(await first.PollOnceAsync(instance));
                source.Set(new FakeEntry("resident", "v1", CloudItemKind.File, "resident.bin", 3),
                    new FakeEntry("sibling", "v1", CloudItemKind.File, "sibling.txt", 1));
                await Assert.ThrowsExactlyAsync<IOException>(() => first.PollOnceAsync(instance).AsTask());
            }
            Assert.IsNotNull(captured);
            MirrorPulsePendingRemotePoll retained = (await pendingStore.LoadAsync(instance, CancellationToken.None))!;
            Assert.AreEqual(2, retained.ProjectionVersion);
            CloudPlaceholderIdentity identity = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "resident", "v1").ToCfSharp();
            var legacyResidentChange = new CloudRemoteChange($"{instance}/resident/{Convert.ToHexString(captured.FinalCursor.Span)}",
                CloudRemoteChangeKind.FileUpsert, identity.RemoteId, "v1", CloudItemKind.File, "Files\\resident.bin",
                identity.ItemId, "v1", null, 3,
                CloudPlaceholderMetadata.CreateFileBuilder().WithLastWriteTime(new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).Build(),
                captured.FinalCursor);
            var legacyBatch = new CloudRemoteChangeBatch(captured.BatchId, captured.InitialCursor,
                [legacyResidentChange, captured.Changes.Single()], captured.FinalCursor);
            // Emulate an actual pre-upgrade payload: no projection-version property.
            byte[] legacyPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                batchId = retained.BatchId,
                fingerprint = legacyBatch.Fingerprint.ToArray(),
                rootDirectoryName = retained.RootDirectoryName,
                previous = retained.Previous,
            }, jsonOptions);
            await pendingStore.ClearAsync(instance, retained.BatchId, CancellationToken.None);
            await catalog.SavePendingRemoteBatchAsync(new(instance, retained.BatchId, legacyPayload,
                JsonSerializer.SerializeToUtf8Bytes(retained.Candidate, jsonOptions)));
            Assert.AreEqual(1, (await pendingStore.LoadAsync(instance, CancellationToken.None))!.ProjectionVersion);
            CollectionAssert.AreEqual(legacyPayload, (await catalog.ReadPendingRemoteBatchAsync(instance))!.Payload);
            int reads = source.Reads;
            var replayed = new List<CloudRemoteChangeBatch>();
            await using var reopened = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
            {
                replayed.Add(batch);
                return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
            }, snapshotStore: snapshots, pendingStore: pendingStore);
            Assert.IsTrue(await reopened.PollOnceAsync(instance));
            Assert.AreEqual(reads, source.Reads, "Recovery must settle the original batch before enumerating again.");
            CollectionAssert.AreEqual(legacyBatch.Fingerprint.ToArray(), replayed.Single().Fingerprint.ToArray());
            Assert.HasCount(2, replayed.Single().Changes);
            source.Set(new FakeEntry("resident", "v1", CloudItemKind.File, "resident.bin", 3),
                new FakeEntry("sibling", "v1", CloudItemKind.File, "sibling.txt", 1),
                new FakeEntry("third", "v1", CloudItemKind.File, "third.txt", 1));
            Assert.IsTrue(await reopened.PollOnceAsync(instance));
            Assert.AreEqual("Files\\third.txt", replayed[1].Changes.Single().RelativePath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task NamespaceFencePreventsEnumerationUntilRecoveryReleasesTheRoot()
    {
        AdapterId adapter = AdapterId.Parse("example.poll-fence");
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(adapter, instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var configured = new AdapterInstance(adapter, InstallId.New(), instance, "Files",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        bool allowed = false;
        int applies = 0;
        await using var poller = new MirrorPulseActiveRemotePoller(source, [configured], [root],
            (_, batch, _) =>
            {
                applies++;
                return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
            }, mayPoll: (_, _) => ValueTask.FromResult(allowed));
        source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 1));
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        Assert.AreEqual(0, source.Reads);
        Assert.AreEqual(0, applies);
        allowed = true;
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 2));
        allowed = false;
        int reads = source.Reads;
        Assert.IsFalse(await poller.PollOnceAsync(instance));
        Assert.AreEqual(reads, source.Reads);
        Assert.AreEqual(0, applies);
        allowed = true;
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        Assert.AreEqual(1, applies);
    }

    [TestMethod]
    public async Task ConcurrentRefreshSharesPollScheduleWithoutBlockingAnotherInstance()
    {
        InstanceId first = InstanceId.New();
        InstanceId second = InstanceId.New();
        AdapterId adapterId = AdapterId.Parse("example.schedule");
        AdapterInstance CreateInstance(InstanceId id) => new(adapterId, InstallId.New(), id, "Scheduled",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        RootRegistration CreateRoot(InstanceId id, string label) => AdapterRootRegistrationMapper.Map(
            adapterId, id, new AdapterRootDefinition("files", label, label, false), RootRegistrationState.Active);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeDirectorySource();
        int firstApplies = 0;
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var poller = new MirrorPulseActiveRemotePoller(source,
            [CreateInstance(first), CreateInstance(second)], [CreateRoot(first, "First"), CreateRoot(second, "Second")],
            async (id, batch, token) =>
            {
                if (id == first)
                {
                    Interlocked.Increment(ref firstApplies);
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                }
                return new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor);
            }, scheduler: scheduler);
        source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 1));
        Assert.IsFalse(await poller.PollOnceAsync(first));
        Assert.IsFalse(await poller.PollOnceAsync(second));
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 2));
        Task<bool> background = poller.PollOnceAsync(first).AsTask();
        await entered.Task;
        Task<bool> refresh = poller.PollOnceAsync(first).AsTask();
        Assert.IsTrue(await poller.PollOnceAsync(second));
        Assert.IsFalse(refresh.IsCompleted);
        release.SetResult();
        Assert.IsTrue(await background);
        Assert.IsFalse(await refresh);
        Assert.AreEqual(1, firstApplies);
    }

    [TestMethod]
    public async Task PollerTurnsRemoteSnapshotChangesIntoOrderedBatches()
    {
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.drive"), instance,
            new AdapterRootDefinition("files", "Documents", "Documents", false),
            RootRegistrationState.Active);
        var source = new FakeDirectorySource();
        var batches = new List<CloudRemoteChangeBatch>();
        var adapter = new AdapterInstance(
            root.AdapterId, InstallId.New(), instance, "Remote files", new Dictionary<string, string>(), [],
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "files"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "transfers"),
            true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        await using var poller = new MirrorPulseActiveRemotePoller(
            source, [adapter], [root],
            (id, batch, _) =>
            {
                Assert.AreEqual(instance, id);
                batches.Add(batch);
                return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
            });

        source.Set(new FakeEntry("file-1", "v1", CloudItemKind.File, "report.bin", 3));
        Assert.IsFalse(await poller.PollOnceAsync(instance));

        source.Set(new FakeEntry("file-1", "v2", CloudItemKind.File, "renamed.bin", 4));
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        Assert.HasCount(1, batches);
        Assert.AreEqual(CloudRemoteChangeKind.Move, batches[0].Changes.Single().Kind);
        Assert.AreEqual("Documents\\renamed.bin", batches[0].Changes.Single().RelativePath);
        Assert.AreEqual("Documents\\report.bin", batches[0].Changes.Single().PreviousRelativePath);
        Assert.AreEqual("v1", batches[0].Changes.Single().PreviousRemoteRevision);

        source.Set();
        Assert.IsTrue(await poller.PollOnceAsync(instance));
        Assert.AreEqual(CloudRemoteChangeKind.Delete, batches[1].Changes.Single().Kind);
        Assert.AreEqual("v2", batches[1].Changes.Single().RemoteRevision);
        CollectionAssert.AreEqual(batches[0].FinalCursor.ToArray(), batches[0].Changes[0].CursorAfter.ToArray());
    }

    [TestMethod]
    public async Task PollerRestoresSnapshotAcrossProcessInstances()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            InstanceId instance = InstanceId.New();
            RootRegistration root = AdapterRootRegistrationMapper.Map(
                AdapterId.Parse("example.drive"), instance,
                new AdapterRootDefinition("files", "Documents", "Documents", false),
                RootRegistrationState.Active);
            var adapter = new AdapterInstance(
                root.AdapterId, InstallId.New(), instance, "Remote files", new Dictionary<string, string>(), [],
                Path.Combine(dataRoot, "files"), Path.Combine(dataRoot, "transfers"), true,
                AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            var source = new FakeDirectorySource();
            var store = new MirrorPulseFileRemotePollSnapshotStore(dataRoot);
            source.Set(new FakeEntry("file-1", "v1", CloudItemKind.File, "report.bin", 3));
            await using (var first = new MirrorPulseActiveRemotePoller(
                source, [adapter], [root], (_, batch, _) => ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)),
                snapshotStore: store))
            {
                Assert.IsFalse(await first.PollOnceAsync(instance));
            }

            var batches = new List<CloudRemoteChangeBatch>();
            await using (var second = new MirrorPulseActiveRemotePoller(
                source, [adapter], [root], (_, batch, _) =>
                {
                    batches.Add(batch);
                    return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
                }, snapshotStore: store))
            {
                Assert.IsFalse(await second.PollOnceAsync(instance));
            }

            source.Set(new FakeEntry("file-1", "v2", CloudItemKind.File, "renamed.bin", 4));
            await using (var third = new MirrorPulseActiveRemotePoller(
                source, [adapter], [root], (_, batch, _) =>
                {
                    batches.Add(batch);
                    return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
                }, snapshotStore: store))
            {
                Assert.IsTrue(await third.PollOnceAsync(instance));
            }

            Assert.AreEqual(CloudRemoteChangeKind.Move, batches.Single().Changes.Single().Kind);
            Assert.AreEqual("Documents\\report.bin", batches.Single().Changes.Single().PreviousRelativePath);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task PollerLeavesMultiRootInstancesForExplicitRemoteRootMapping()
    {
        InstanceId instance = InstanceId.New();
        AdapterId adapterId = AdapterId.Parse("example.multi-root");
        IReadOnlyList<RootRegistration> roots = AdapterRootRegistrationMapper.MapAll(
            adapterId, instance,
            [new AdapterRootDefinition("documents", "Documents", "Documents", false),
             new AdapterRootDefinition("photos", "Photos", "Photos", false)],
            RootRegistrationState.Active);
        var adapter = new AdapterInstance(
            adapterId, InstallId.New(), instance, "Multi-root", new Dictionary<string, string>(), [],
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "files"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "transfers"),
            true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        await using var poller = new MirrorPulseActiveRemotePoller(
            new FakeDirectorySource(), [adapter], roots, (_, batch, _) => ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)));

        Assert.IsFalse(await poller.PollOnceAsync(instance));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task PollerRetainsOldSnapshotUntilApplyCompletesWithFinalCursor(bool completed, bool finalCursor)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.retry"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Retry", new Dictionary<string, string>(), [],
            Path.Combine(dataRoot, "files"), Path.Combine(dataRoot, "transfers"), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var store = new MirrorPulseFileRemotePollSnapshotStore(dataRoot);
        var batches = new List<CloudRemoteChangeBatch>();
        bool retry = true;
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(
                new MirrorPulseStoragePaths(Path.Combine(dataRoot, "sync"), Path.Combine(dataRoot, "data")));
            var pendingStore = new MirrorPulseCatalogRemotePollPendingStore(catalog);
            await using var poller = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
            {
                batches.Add(batch);
                return ValueTask.FromResult(retry
                    ? new MirrorPulseRemotePollApplyOutcome(completed, finalCursor ? batch.FinalCursor : batch.InitialCursor)
                    : new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
            }, snapshotStore: store, pendingStore: pendingStore);
            source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
            Assert.IsFalse(await poller.PollOnceAsync(instance));
            source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
            if (completed) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => poller.PollOnceAsync(instance).AsTask());
            else Assert.IsFalse(await poller.PollOnceAsync(instance));
            Assert.AreEqual("v1", (await store.LoadAsync(instance))!["file"].RemoteRevision);
            MirrorPulsePendingRemotePoll pending = (await pendingStore.LoadAsync(instance, CancellationToken.None))!;
            Assert.AreEqual(batches[0].BatchId, pending.BatchId);
            Assert.AreEqual("v2", pending.Candidate["file"].RemoteRevision);
            retry = false;
            Assert.IsTrue(await poller.PollOnceAsync(instance));
            Assert.HasCount(2, batches);
            Assert.AreEqual(batches[0].BatchId, batches[1].BatchId);
            CollectionAssert.AreEqual(batches[0].Fingerprint.ToArray(), batches[1].Fingerprint.ToArray());
            Assert.AreEqual("v2", (await store.LoadAsync(instance))!["file"].RemoteRevision);
            Assert.IsNull(await pendingStore.LoadAsync(instance, CancellationToken.None));
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
    }

    [TestMethod]
    [DataRow("before-apply")]
    [DataRow("after-apply")]
    [DataRow("snapshot-commit")]
    [DataRow("pending-clear")]
    public async Task ReopenedPollerReplaysPendingBeforeObservingNewRemoteChanges(string fault)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-replay-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(dataRoot, "sync"), Path.Combine(dataRoot, "data"));
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.replay"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Replay", new Dictionary<string, string>(), [],
            Path.Combine(dataRoot, "files"), Path.Combine(dataRoot, "transfers"), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
        var observed = new List<CloudRemoteChangeBatch>();
        var applied = new HashSet<string>(StringComparer.Ordinal);
        Guid renameOperation = Guid.Empty;
        bool inject = true;
        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        {
            observed.Add(batch);
            if (inject && fault == "before-apply") throw new IOException("fixture before apply");
            applied.Add(batch.BatchId); // Emulates an idempotent apply boundary; native replay is verified separately.
            if (inject && fault == "after-apply") throw new IOException("fixture after apply");
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }
        try
        {
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var manifest = new AdapterManifest(1, root.AdapterId, "Replay", "1.0.0", new(1, 1),
                    new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                    new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                    [new("files", "Files", "Files", false)]);
                var installation = new InstalledAdapter(manifest, adapter.InstallId, Path.Combine(dataRoot, "installed"),
                    new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                await catalog.SaveAdapterTopologyAsync(new([installation], [adapter], [root]));
                var pending = new MirrorPulseCatalogRemotePollPendingStore(catalog);
                await using var first = new MirrorPulseActiveRemotePoller(source, [adapter], [root], Apply,
                    snapshotStore: new FaultingSnapshotStore(snapshots, fault == "snapshot-commit"),
                    pendingStore: new FaultingPendingStore(pending, fault == "pending-clear"));
                source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
                Assert.IsFalse(await first.PollOnceAsync(instance));
                source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
                await Assert.ThrowsExactlyAsync<IOException>(() => first.PollOnceAsync(instance).AsTask());
                Assert.IsNotNull(await pending.LoadAsync(instance, CancellationToken.None));
                renameOperation = (await catalog.PrepareManagedRootRenameAsync(root.RootId, "Renamed")).OperationId;
            }

            inject = false;
            source.Set(new FakeEntry("file", "v3", CloudItemKind.File, "file.bin", 5));
            int readsBeforeReplay = source.Reads;
            await using (MirrorPulseProductCatalog reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var pending = new MirrorPulseCatalogRemotePollPendingStore(reopened);
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
                var fence = new MirrorPulseRemoteNamespaceFence(router, reopened);
                await using var second = new MirrorPulseActiveRemotePoller(source, [adapter], [root], Apply,
                    snapshotStore: snapshots, pendingStore: pending, mayPoll: fence.CanPollAsync);
                MirrorPulsePendingRemotePoll retained = (await pending.LoadAsync(instance, CancellationToken.None))!;
                Assert.IsFalse(await second.PollOnceAsync(instance));
                Assert.AreEqual(readsBeforeReplay, source.Reads);
                Assert.HasCount(1, observed);
                MirrorPulsePendingRemotePoll fenced = (await pending.LoadAsync(instance, CancellationToken.None))!;
                Assert.AreEqual(retained.BatchId, fenced.BatchId);
                CollectionAssert.AreEqual(retained.Fingerprint, fenced.Fingerprint);
                Assert.AreEqual("Files", fenced.RootDirectoryName);
                await reopened.TransitionManagedRootRenameAsync(renameOperation,
                    MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
                Assert.IsTrue(await second.PollOnceAsync(instance));
                Assert.AreEqual(readsBeforeReplay, source.Reads);
                Assert.HasCount(2, observed);
                Assert.AreEqual(observed[0].BatchId, observed[1].BatchId);
                CollectionAssert.AreEqual(observed[0].Fingerprint.ToArray(), observed[1].Fingerprint.ToArray());
                CollectionAssert.AreEqual(observed[0].InitialCursor.ToArray(), observed[1].InitialCursor.ToArray());
                CollectionAssert.AreEqual(observed[0].FinalCursor.ToArray(), observed[1].FinalCursor.ToArray());
                Assert.HasCount(1, applied);
                Assert.IsNull(await pending.LoadAsync(instance, CancellationToken.None));
                Assert.AreEqual("v2", (await snapshots.LoadAsync(instance))!["file"].RemoteRevision);
                Assert.IsTrue(await second.PollOnceAsync(instance));
                CollectionAssert.AreEqual(observed[1].FinalCursor.ToArray(), observed[2].InitialCursor.ToArray());
                Assert.AreEqual("v3", (await snapshots.LoadAsync(instance))!["file"].RemoteRevision);
            }
        }
        finally { Directory.Delete(dataRoot, true); }
    }

    private sealed class FaultingSnapshotStore(IMirrorPulseRemotePollSnapshotStore inner, bool fail) : IMirrorPulseRemotePollSnapshotStore
    {
        public ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(
            InstanceId instanceId, CancellationToken cancellationToken = default) => inner.LoadAsync(instanceId, cancellationToken);
        public async ValueTask SaveAsync(InstanceId instanceId, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot,
            CancellationToken cancellationToken = default)
        {
            await inner.SaveAsync(instanceId, snapshot, cancellationToken);
            if (fail && snapshot.Values.Any(entry => entry.RemoteRevision == "v2")) throw new IOException("fixture after snapshot commit");
        }
    }

    private sealed class FaultingPendingStore(IMirrorPulseRemotePollPendingStore inner, bool fail) : IMirrorPulseRemotePollPendingStore
    {
        public ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(InstanceId instanceId, CancellationToken cancellationToken) => inner.LoadAsync(instanceId, cancellationToken);
        public ValueTask SaveAsync(InstanceId instanceId, MirrorPulsePendingRemotePoll pending, CancellationToken cancellationToken) => inner.SaveAsync(instanceId, pending, cancellationToken);
        public ValueTask ClearAsync(InstanceId instanceId, string batchId, CancellationToken cancellationToken) =>
            fail ? throw new IOException("fixture before pending clear") : inner.ClearAsync(instanceId, batchId, cancellationToken);
    }

    private sealed class FakeDirectorySource : IMirrorPulseDirectoryPageSource
    {
        private readonly Dictionary<string, CloudRemoteDirectoryEntry> _entries = new(StringComparer.Ordinal);
        public int Reads { get; private set; }

        public void Set(params FakeEntry[] entries)
        {
            _entries.Clear();
            foreach (FakeEntry entry in entries)
            {
                CloudPlaceholderMetadata metadata = entry.Kind == CloudItemKind.Directory
                    ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()
                    : CloudPlaceholderMetadata.CreateFileBuilder().Build();
                if (entry.Kind == CloudItemKind.File)
                {
                    metadata = CloudPlaceholderMetadata.CreateFileBuilder()
                        .WithLastWriteTime(entry.LastWriteTime ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
                        .Build();
                }
                _entries.Add(entry.RemoteId, new CloudRemoteDirectoryEntry(entry.RemoteId,
                    entry.Revision, entry.Kind, entry.Path, null, entry.Length, metadata, false));
            }
        }

        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
            InstanceId instanceId,
            string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            Reads++;
            CloudRemoteDirectoryEntry[] entries = _entries.Values
                .Where(entry => !entry.RelativePath.Contains('/'))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult(new CloudRemoteDirectoryPage(entries,
                ReadOnlyMemory<byte>.Empty, true));
        }
    }

    private sealed record FakeEntry(
        string RemoteId,
        string Revision,
        CloudItemKind Kind,
        string Path,
        long Length, DateTimeOffset? LastWriteTime = null);
}
