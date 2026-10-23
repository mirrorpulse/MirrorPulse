using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteGenerationPollerRetainsSuccessorBirthAcrossOwnersThenUpdatesAndDeletesItsCapturedAddress(bool scoped)
    {
        using var fixture = new Fixture();
        var source = new GenerationPollSource(); var batches = new List<CloudRemoteChangeBatch>();
        RootRegistration root; CloudRemoteChangeBatch original; CloudPlaceholderIdentity successor; string history;
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(fixture.Paths.DataRootPath);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); root = prepared.Root;
            var instance = (await catalog.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
            var pending = new MirrorPulseCatalogRemotePollPendingStore(catalog);
            await using (var legacy = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply,
                snapshotStore: snapshots, pendingStore: pending))
            {
                Assert.IsFalse(await legacy.PollOnceAsync(root.InstanceId));
                source.Revision = "v2"; Assert.IsTrue(await legacy.PollOnceAsync(root.InstanceId));
            }
            var predecessor = batches.Single();
            var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
            var start = await StartTransferAckAsync(catalog, target);
            var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
            history = await ReadTransferRetirementBytesAsync(fixture.Paths);
            var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)));
            await using var next = new MirrorPulseActiveRemotePoller(source, [instance], [root], (_, batch, _) =>
            {
                batches.Add(batch); throw new IOException("Interrupted after durable generation projection, before native apply.");
            }, snapshotStore: snapshots, pendingStore: pending, identityResolver: resolver);
            await Assert.ThrowsExactlyAsync<IOException>(() => next.PollOnceAsync(root.InstanceId).AsTask());
            original = batches.Last(); var birth = original.Changes.Single();
            successor = await resolver.ResolveAsync(root, "backend-file", "v2");
            Assert.AreEqual(successor.ItemId, birth.ItemId); Assert.AreEqual(successor.RemoteId, birth.RemoteId);
            Assert.AreNotEqual(prepared.Plan.Source.Identity.ItemId, birth.ItemId);
            Assert.IsNull(birth.PreviousRemoteRevision); Assert.IsNull(birth.PreviousRelativePath);
            Assert.AreEqual(CloudRemoteChangeKind.FileUpsert, birth.Kind);
            CollectionAssert.AreEqual(predecessor.FinalCursor.ToArray(), original.InitialCursor.ToArray());
            Assert.IsFalse(original.InitialCursor.Span.SequenceEqual(original.FinalCursor.Span), "A new generation changes the cursor even when backend metadata did not change.");
            var retained = (await pending.LoadAsync(root, default))!;
            Assert.AreEqual(4, retained.ProjectionVersion);
            Assert.IsNull(retained.Previous["backend-file"].PublicIdentity);
            Assert.AreEqual(successor.ItemId, retained.Candidate["backend-file"].PublicIdentity!.ItemId);
            Assert.AreEqual(history, await ReadTransferRetirementBytesAsync(fixture.Paths));
        }
        int reads = source.Reads; source.Revision = "v3";
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var instance = (await reopened.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
            await using var poller = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply, snapshotStore: snapshots,
                pendingStore: new MirrorPulseCatalogRemotePollPendingStore(reopened), identityResolver: new(reopened));
            Assert.IsTrue(await poller.PollOnceAsync(root.InstanceId)); Assert.AreEqual(reads, source.Reads);
            var replay = batches.Last(); Assert.AreEqual(original.BatchId, replay.BatchId);
            CollectionAssert.AreEqual(original.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
            Assert.AreEqual("v2", (await snapshots.LoadAsync(root))!["backend-file"].RemoteRevision);
            Assert.IsTrue(await poller.PollOnceAsync(root.InstanceId));
            var update = batches.Last(); Assert.AreEqual(successor.ItemId, update.Changes.Single().ItemId);
            Assert.AreEqual("v2", update.Changes.Single().PreviousRemoteRevision);
            CollectionAssert.AreEqual(replay.FinalCursor.ToArray(), update.InitialCursor.ToArray());
            source.Present = false; Assert.IsTrue(await poller.PollOnceAsync(root.InstanceId));
            var deletion = batches.Last().Changes.Single(); Assert.AreEqual(CloudRemoteChangeKind.Delete, deletion.Kind);
            Assert.AreEqual(successor.ItemId, deletion.ItemId); Assert.AreEqual(successor.RemoteId, deletion.RemoteId);
            Assert.AreEqual("v3", deletion.RemoteRevision);
            Assert.IsNull(await new MirrorPulseCatalogRemotePollPendingStore(reopened).LoadAsync(root, default));
            Assert.AreEqual(history, await ReadTransferRetirementBytesAsync(fixture.Paths));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));

        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        { batches.Add(batch); return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteGenerationPollerUpgradesAnUnchangedLegacySnapshotWithoutChangingItsCursor(bool scoped)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); var root = prepared.Root;
        var instance = (await catalog.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(fixture.Paths.DataRootPath); var pending = new MirrorPulseCatalogRemotePollPendingStore(catalog);
        var source = new GenerationPollSource(); var batches = new List<CloudRemoteChangeBatch>();
        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        { batches.Add(batch); return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)); }
        await using (var legacy = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply, snapshotStore: snapshots, pendingStore: pending))
        {
            Assert.IsFalse(await legacy.PollOnceAsync(root.InstanceId)); source.Revision = "v2";
            Assert.IsTrue(await legacy.PollOnceAsync(root.InstanceId));
        }
        var original = batches.Single();
        Assert.IsNull((await snapshots.LoadAsync(root))!["backend-file"].PublicIdentity);
        await using var next = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply, snapshotStore: snapshots,
            pendingStore: pending, identityResolver: new(catalog));
        Assert.IsFalse(await next.PollOnceAsync(root.InstanceId)); Assert.HasCount(1, batches);
        Assert.AreEqual(prepared.Plan.Source.Identity.ItemId, (await snapshots.LoadAsync(root))!["backend-file"].PublicIdentity!.ItemId);
        source.Revision = "v3"; Assert.IsTrue(await next.PollOnceAsync(root.InstanceId));
        CollectionAssert.AreEqual(original.FinalCursor.ToArray(), batches.Last().InitialCursor.ToArray());
        Assert.AreEqual("v2", batches.Last().Changes.Single().PreviousRemoteRevision);
        Assert.AreEqual(original.Changes.Single().ItemId, batches.Last().Changes.Single().ItemId);
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.Changes.Single().ItemId!.Value));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteGenerationPollerReplaysOriginalV3AfterASuccessorWasSelectedWithoutReadingTheNewTree(bool scoped)
    {
        using var fixture = new Fixture(); var source = new GenerationPollSource(); RootRegistration root; CloudRemoteChangeBatch original = null!;
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(fixture.Paths.DataRootPath);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); root = prepared.Root;
            var instance = (await catalog.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
            await using (var legacy = new MirrorPulseActiveRemotePoller(source, [instance], [root], (_, batch, _) =>
            { original = batch; throw new IOException("Retain exact v3 intent."); }, snapshotStore: snapshots, pendingStore: new MirrorPulseCatalogRemotePollPendingStore(catalog)))
            {
                Assert.IsFalse(await legacy.PollOnceAsync(root.InstanceId)); source.Revision = "v2";
                await Assert.ThrowsExactlyAsync<IOException>(() => legacy.PollOnceAsync(root.InstanceId).AsTask());
            }
            var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
            var start = await StartTransferAckAsync(catalog, target);
            var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
            var selected = await new MirrorPulseRemoteIdentityResolver(catalog,
                new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1))).ResolveAsync(root, "backend-file", "later");
            Assert.AreNotEqual(original.Changes.Single().ItemId, selected.ItemId);
        }
        int reads = source.Reads; source.Revision = "newer-tree";
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var owner = (await reopened.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
        await using var next = new MirrorPulseActiveRemotePoller(source, [owner], [root], (_, batch, _) =>
        {
            Assert.AreEqual(original.BatchId, batch.BatchId); CollectionAssert.AreEqual(original.Fingerprint.ToArray(), batch.Fingerprint.ToArray());
            Assert.AreEqual(original.Changes.Single().ItemId, batch.Changes.Single().ItemId);
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }, snapshotStore: snapshots, pendingStore: new MirrorPulseCatalogRemotePollPendingStore(reopened), identityResolver: new(reopened));
        Assert.IsTrue(await next.PollOnceAsync(root.InstanceId)); Assert.AreEqual(reads, source.Reads);
        Assert.AreEqual("v2", (await snapshots.LoadAsync(root))!["backend-file"].RemoteRevision);
    }

    private sealed class GenerationPollSource : IMirrorPulseDirectoryPageSource
    {
        internal string Revision = "accepted";
        internal bool Present = true;
        internal int Reads;
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(new CloudRemoteDirectoryPage(Present ?
                [new CloudRemoteDirectoryEntry("backend-file", Revision, CloudItemKind.File, "new.txt", null, 4,
                    CloudPlaceholderMetadata.CreateFileBuilder().Build(), false)] : [], ReadOnlyMemory<byte>.Empty, true));
        }
    }
}
