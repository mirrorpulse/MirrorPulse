using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task IdenticalObjectsInTwoRootsHaveIndependentBatchesIdentitiesAndReopenedSnapshots()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        await using (var poller = fixture.Poller())
        {
            Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
            fixture.Source.Set(fixture.First, "v2"); fixture.Source.Set(fixture.Second, "v2");
            Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance)); Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance));
        }
        Assert.HasCount(2, fixture.Applied);
        var first = fixture.Applied[0]; var second = fixture.Applied[1];
        Assert.AreNotEqual(first.BatchId, second.BatchId);
        Assert.AreNotEqual(first.Changes.Single().ChangeId, second.Changes.Single().ChangeId);
        Assert.AreNotEqual(first.Changes.Single().ItemId, second.Changes.Single().ItemId);
        Assert.IsFalse(first.FinalCursor.Span.SequenceEqual(second.FinalCursor.Span));
        Assert.AreEqual(fixture.First.DirectoryName + "\\file.bin", first.Changes.Single().RelativePath);
        Assert.AreEqual(fixture.Second.DirectoryName + "\\file.bin", second.Changes.Single().RelativePath);
        Assert.AreEqual("v2", (await fixture.Snapshots.LoadAsync(fixture.First))!["same"].RemoteRevision);
        Assert.AreEqual("v2", (await fixture.Snapshots.LoadAsync(fixture.Second))!["same"].RemoteRevision);
        Assert.IsNull(await fixture.Snapshots.LoadAsync(fixture.Instance));
        await fixture.ReopenCatalogAsync();
        await using var reopened = fixture.Poller();
        Assert.IsFalse(await reopened.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await reopened.PollOnceAsync(fixture.Instance));
        Assert.HasCount(2, fixture.Applied);
    }

    [TestMethod]
    public async Task ARejectedOrFencedRootDoesNotStarveItsSiblingAndReplaysItsOriginalBatchWithoutSourceReads()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        bool fail = true, replay = false;
        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        {
            fixture.Applied.Add(batch);
            return fail && batch.BatchId.StartsWith(MirrorPulseRemotePollBatchScope.Prefix(fixture.First), StringComparison.Ordinal)
                ? ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(new IOException("Original root effect interrupted."))
                : ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }
        await using (var first = fixture.Poller(Apply))
        {
            Assert.IsFalse(await first.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await first.PollOnceAsync(fixture.Instance));
            fixture.Source.Set(fixture.First, "v2"); fixture.Source.Set(fixture.Second, "v2");
            await Assert.ThrowsExactlyAsync<IOException>(() => first.PollOnceAsync(fixture.Instance).AsTask());
            Assert.IsTrue(await first.PollOnceAsync(fixture.Instance));
        }
        var original = (await fixture.Pending.LoadAsync(fixture.First, default))!;
        Assert.IsNotNull(await fixture.Catalog.ReadPendingRemoteBatchAsync(fixture.Instance), "Streamed apply must still see a pending polling owner.");
        Assert.IsNull(await fixture.Pending.LoadAsync(fixture.Second, default));
        await fixture.ReopenCatalogAsync(); fail = false;
        fixture.Source.Set(fixture.First, "v3"); fixture.Source.Set(fixture.Second, "v3");
        await using var reopened = fixture.Poller(Apply, mayReplay: (_, _, _) => ValueTask.FromResult(replay));
        int reads = fixture.Source.Calls.Count;
        Assert.IsFalse(await reopened.PollOnceAsync(fixture.Instance)); Assert.HasCount(reads, fixture.Source.Calls);
        Assert.IsTrue(await reopened.PollOnceAsync(fixture.Instance));
        Assert.AreEqual(fixture.Second.UniquenessKey, fixture.Source.Calls.Last());
        replay = true; reads = fixture.Source.Calls.Count;
        Assert.IsTrue(await reopened.PollOnceAsync(fixture.Instance)); Assert.HasCount(reads, fixture.Source.Calls);
        var recovered = fixture.Applied.Last(); Assert.AreEqual(original.BatchId, recovered.BatchId);
        CollectionAssert.AreEqual(original.Fingerprint, recovered.Fingerprint.ToArray());
        Assert.AreEqual("v2", (await fixture.Snapshots.LoadAsync(fixture.First))!["same"].RemoteRevision);
        Assert.IsNull(await fixture.Catalog.ReadPendingRemoteBatchAsync(fixture.Instance));
    }

    [TestMethod]
    public async Task DisabledAndClosedRootsKeepTheirBaselineWhileTheSameInstanceSiblingContinues()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        await using var work = new MirrorPulseNamespaceWorkAdmission(fixture.Roots.Select(root => root.RootId));
        await OpenPollAdmissionAsync(work, fixture.First.RootId); await OpenPollAdmissionAsync(work, fixture.Second.RootId);
        await using var poller = fixture.Poller(work: work);
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        fixture.Roots = [CopyRoot(fixture.First, RootRegistrationState.Disabled), fixture.Second];
        fixture.Source.Set(fixture.First, "v2"); fixture.Source.Set(fixture.Second, "v2");
        int reads = fixture.Source.Calls.Count;
        Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance));
        Assert.HasCount(reads + 1, fixture.Source.Calls); Assert.AreEqual(fixture.Second.UniquenessKey, fixture.Source.Calls.Last());
        fixture.Roots = [fixture.First, fixture.Second];
        await using (await work.BeginRecoveryAsync([fixture.First.RootId])) { }
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.HasCount(reads + 1, fixture.Source.Calls);
        fixture.Source.Set(fixture.Second, "v3"); Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance));
        await OpenPollAdmissionAsync(work, fixture.First.RootId);
        Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance));
        Assert.AreEqual("v1", fixture.Applied.Last().Changes.Single().PreviousRemoteRevision);
        Assert.AreEqual("v2", fixture.Applied.Last().Changes.Single().RemoteRevision);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingRootCannotAcquireAnotherLabelOrSourceMapping(bool changeSourceKey)
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        await using var poller = fixture.Poller((_, batch, _) => ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(false, batch.InitialCursor)));
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        fixture.Source.Set(fixture.First, "v2"); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        var original = (await fixture.Pending.LoadAsync(fixture.First, default))!;
        fixture.Roots = [CopyRoot(fixture.First, RootRegistrationState.Active,
            changeSourceKey ? null : "Renamed", changeSourceKey ? "different-source" : null), fixture.Second];
        int reads = fixture.Source.Calls.Count;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => poller.PollOnceAsync(fixture.Instance).AsTask());
        Assert.HasCount(reads, fixture.Source.Calls);
        Assert.AreEqual(original.BatchId, (await fixture.Pending.LoadAsync(fixture.First, default))!.BatchId);
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.HasCount(reads + 1, fixture.Source.Calls);
    }

    [TestMethod]
    public async Task AnUnscopedLegacySnapshotIsRetainedInsteadOfBeingCopiedIntoSeveralRoots()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        await fixture.Snapshots.SaveAsync(fixture.Instance, new Dictionary<string, MirrorPulseRemoteSnapshotEntry>());
        await using var poller = fixture.Poller();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => poller.PollOnceAsync(fixture.Instance).AsTask());
        Assert.IsEmpty(fixture.Source.Calls); Assert.IsNotNull(await fixture.Snapshots.LoadAsync(fixture.Instance));
        Assert.IsNull(await fixture.Snapshots.LoadAsync(fixture.First)); Assert.IsNull(await fixture.Snapshots.LoadAsync(fixture.Second));
    }

    [TestMethod]
    public async Task ACommittedSnapshotRejectsAChangedSourceKeyBeforeReadingThatSource()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        await using var poller = fixture.Poller();
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        fixture.Roots = [CopyRoot(fixture.First, RootRegistrationState.Active, key: "changed"), fixture.Second];
        int reads = fixture.Source.Calls.Count;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => poller.PollOnceAsync(fixture.Instance).AsTask());
        Assert.HasCount(reads, fixture.Source.Calls);
    }

    [TestMethod]
    public async Task OpaqueIdsAndRevisionsCannotAliasEachOtherThroughDelimiterCharacters()
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        fixture.Source.Set(fixture.First, "c", "a\nb");
        await using var poller = fixture.Poller();
        Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await poller.PollOnceAsync(fixture.Instance));
        fixture.Source.Set(fixture.First, "b\nc", "a");
        Assert.IsTrue(await poller.PollOnceAsync(fixture.Instance));
        Assert.HasCount(2, fixture.Applied.Single().Changes);
        Assert.AreNotEqual(fixture.Applied.Single().Changes[0].ItemId, fixture.Applied.Single().Changes[1].ItemId);
    }

    [TestMethod]
    [DataRow("before-apply")]
    [DataRow("after-apply")]
    [DataRow("snapshot-commit")]
    [DataRow("pending-clear")]
    public async Task EachRootRecoversItsCommittedIntentAcrossEveryPollCommitGap(string fault)
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        bool inject = true;
        var successful = new HashSet<string>(StringComparer.Ordinal);
        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        {
            fixture.Applied.Add(batch);
            if (inject && fault == "before-apply") throw new IOException("Before apply.");
            successful.Add(batch.BatchId);
            if (inject && fault == "after-apply") throw new IOException("After apply.");
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }
        await using (var first = fixture.Poller(Apply,
            snapshots: new RootFaultingSnapshotStore(fixture.Snapshots, fault == "snapshot-commit"),
            pending: new RootFaultingPendingStore(fixture.Pending, fault == "pending-clear")))
        {
            Assert.IsFalse(await first.PollOnceAsync(fixture.Instance)); Assert.IsFalse(await first.PollOnceAsync(fixture.Instance));
            fixture.Source.Set(fixture.First, "v2");
            await Assert.ThrowsExactlyAsync<IOException>(() => first.PollOnceAsync(fixture.Instance).AsTask());
        }
        var original = (await fixture.Pending.LoadAsync(fixture.First, default))!;
        inject = false; fixture.Source.Set(fixture.First, "v3"); int reads = fixture.Source.Calls.Count;
        await fixture.ReopenCatalogAsync();
        await using var reopened = fixture.Poller(Apply);
        Assert.IsTrue(await reopened.PollOnceAsync(fixture.Instance)); Assert.HasCount(reads, fixture.Source.Calls);
        Assert.AreEqual(original.BatchId, fixture.Applied.Last().BatchId);
        CollectionAssert.AreEqual(original.Fingerprint, fixture.Applied.Last().Fingerprint.ToArray());
        Assert.HasCount(1, successful); Assert.IsNull(await fixture.Pending.LoadAsync(fixture.First, default));
        Assert.AreEqual("v2", (await fixture.Snapshots.LoadAsync(fixture.First))!["same"].RemoteRevision);
        Assert.AreEqual("v1", (await fixture.Snapshots.LoadAsync(fixture.Second))!["same"].RemoteRevision);
        Assert.IsFalse(await reopened.PollOnceAsync(fixture.Instance)); Assert.IsTrue(await reopened.PollOnceAsync(fixture.Instance));
        Assert.AreEqual("v2", fixture.Applied.Last().Changes.Single().PreviousRemoteRevision);
        CollectionAssert.AreEqual(fixture.Applied[0].FinalCursor.ToArray(), fixture.Applied.Last().InitialCursor.ToArray());
    }

    private static RootRegistration CopyRoot(RootRegistration root, RootRegistrationState state, string? label = null, string? key = null) =>
        new(root.AdapterId, root.InstanceId, root.RootId, key ?? root.UniquenessKey, label ?? root.DirectoryName,
            label ?? root.Label, root.CustomEntry, state, root.RegisteredAt, root.IdentityScope);

    [TestMethod]
    [DataRow("cross-root")]
    [DataRow("disabled")]
    [DataRow("missing-root")]
    [DataRow("cursor")]
    public async Task RootCursorScopeStillRequiresItsCurrentMappingAndOriginalCursorIdentity(string fault)
    {
        await using var fixture = await RootPollFixture.CreateAsync();
        byte[] initial = new byte[32], final = Enumerable.Repeat((byte)1, 32).ToArray();
        var pathRoot = fault == "cross-root" ? fixture.Second : fixture.First;
        var change = new CloudRemoteChange("change", CloudRemoteChangeKind.FileUpsert, "file", "v1", CloudItemKind.File,
            pathRoot.DirectoryName + "\\file.bin", Guid.NewGuid(), length: 1, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        var batch = new CloudRemoteChangeBatch(MirrorPulseRemotePollBatchScope.Prefix(fixture.First) +
            Convert.ToHexString(initial) + "/" + Convert.ToHexString(final), fault == "cursor" ? final : initial, [change], final);
        var roots = fault == "disabled" ? new[] { CopyRoot(fixture.First, RootRegistrationState.Disabled), fixture.Second } :
            fault == "missing-root" ? new[] { fixture.Second } : fixture.Roots;
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, roots);
        if (fault == "disabled") Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseRemotePollBatchScope.RequireCurrentMapping(fixture.Instance, batch, router));
        else Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemotePollBatchScope.RequireCurrentMapping(fixture.Instance, batch, router));
        Assert.IsEmpty(fixture.Source.Calls);
    }

    private sealed class RootPollFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-poll", Guid.NewGuid().ToString("N"));
        private RootPollFixture() { }
        internal InstanceId Instance { get; } = InstanceId.New();
        internal RootRegistration[] Roots { get; set; } = [];
        internal RootRegistration First { get; private set; } = null!;
        internal RootRegistration Second { get; private set; } = null!;
        internal MirrorPulseStoragePaths Paths { get; private set; } = null!;
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal MirrorPulseFileRemotePollSnapshotStore Snapshots { get; private set; } = null!;
        internal MirrorPulseCatalogRemotePollPendingStore Pending => new(Catalog);
        internal RootPollSource Source { get; } = new();
        internal List<CloudRemoteChangeBatch> Applied { get; } = [];

        internal static async Task<RootPollFixture> CreateAsync()
        {
            var fixture = new RootPollFixture();
            fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            fixture.Snapshots = new(fixture.Paths.DataRootPath);
            fixture.Roots = AdapterRootRegistrationMapper.MapAll(AdapterId.Parse("example.root-poll"), fixture.Instance,
                [new("first", "First", "First", false), new("second", "Second", "Second", false)], RootRegistrationState.Active,
                identityScope: RootIdentityScope.InstanceRoot)
                .OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal).ToArray();
            fixture.First = fixture.Roots[0]; fixture.Second = fixture.Roots[1];
            fixture.Source.Set(fixture.First, "v1"); fixture.Source.Set(fixture.Second, "v1");
            return fixture;
        }

        internal MirrorPulseActiveRemotePoller Poller(Func<InstanceId, CloudRemoteChangeBatch, CancellationToken, ValueTask<MirrorPulseRemotePollApplyOutcome>>? apply = null,
            MirrorPulseNamespaceWorkAdmission? work = null,
            Func<RootRegistration, CloudRemoteChangeBatch, CancellationToken, ValueTask<bool>>? mayReplay = null,
            IMirrorPulseRemotePollSnapshotStore? snapshots = null, IMirrorPulseRemotePollPendingStore? pending = null) =>
            new(Source, [AdmissionAdapter(First)], Roots, apply ?? ((_, batch, _) =>
            { Applied.Add(batch); return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)); }),
                snapshotStore: snapshots ?? Snapshots, pendingStore: pending ?? Pending, currentRoots: () => Roots, namespaceWork: work, mayReplay: mayReplay);

        internal async Task ReopenCatalogAsync() { await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths); }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }

    private sealed class RootPollSource : IMirrorPulseDirectoryPageSource
    {
        private readonly Dictionary<string, FakeDirectorySource> _sources = new(StringComparer.Ordinal);
        internal List<string> Calls { get; } = [];
        internal void Set(RootRegistration root, string revision, string remoteId = "same")
        {
            var source = new FakeDirectorySource(); source.Set(new FakeEntry(remoteId, revision, CloudItemKind.File, "file.bin", 1));
            _sources[root.UniquenessKey] = source;
        }
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string path, ReadOnlyMemory<byte> cursor, int size, CancellationToken token) =>
            throw new InvalidOperationException("Root polling must identify its Worker root.");
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string rootKey, string path, ReadOnlyMemory<byte> cursor, int size, CancellationToken token)
        { Calls.Add(rootKey); return _sources[rootKey].ReadPageAsync(instanceId, path, cursor, size, token); }
    }

    private sealed class RootFaultingSnapshotStore(IMirrorPulseRootRemotePollSnapshotStore inner, bool fail) : IMirrorPulseRootRemotePollSnapshotStore
    {
        public ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(InstanceId instance, CancellationToken token = default) => inner.LoadAsync(instance, token);
        public ValueTask SaveAsync(InstanceId instance, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot, CancellationToken token = default) => inner.SaveAsync(instance, snapshot, token);
        public ValueTask ClearLegacyAsync(InstanceId instance, CancellationToken token = default) => inner.ClearLegacyAsync(instance, token);
        public ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(RootRegistration root, CancellationToken token = default) => inner.LoadAsync(root, token);
        public async ValueTask SaveAsync(RootRegistration root, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot, CancellationToken token = default)
        {
            await inner.SaveAsync(root, snapshot, token);
            if (fail && snapshot.Values.Any(entry => entry.RemoteRevision == "v2")) throw new IOException("After root snapshot commit.");
        }
    }

    private sealed class RootFaultingPendingStore(IMirrorPulseRootRemotePollPendingStore inner, bool fail) : IMirrorPulseRootRemotePollPendingStore
    {
        public ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(InstanceId instance, CancellationToken token) => inner.LoadAsync(instance, token);
        public ValueTask SaveAsync(InstanceId instance, MirrorPulsePendingRemotePoll pending, CancellationToken token) => inner.SaveAsync(instance, pending, token);
        public ValueTask ClearAsync(InstanceId instance, string batchId, CancellationToken token) => inner.ClearAsync(instance, batchId, token);
        public ValueTask<MirrorPulsePendingRemotePoll?> LoadAsync(RootRegistration root, CancellationToken token) => inner.LoadAsync(root, token);
        public ValueTask SaveAsync(RootRegistration root, MirrorPulsePendingRemotePoll pending, CancellationToken token) => inner.SaveAsync(root, pending, token);
        public ValueTask ClearAsync(RootRegistration root, string batchId, CancellationToken token) =>
            fail ? ValueTask.FromException(new IOException("Before root pending clear.")) : inner.ClearAsync(root, batchId, token);
    }
}
