using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, "captured")]
    [DataRow(true, "captured")]
    [DataRow(false, "sdk-partial")]
    [DataRow(true, "sdk-partial")]
    [DataRow(false, "sdk-complete")]
    [DataRow(true, "sdk-complete")]
    public async Task AdapterGenerationProjectionKeepsPreUpgradeAndCompletedRetriesAtTheirOriginalAddress(bool scoped, string boundary)
    {
        using var fixture = new Fixture(); RootRegistration root; CloudRemoteChangeBatch original; AdapterRemoteChangeBatch source;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        await using (var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath)))
        {
            var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); root = prepared.Root;
            var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root]); source = GenerationWire(root, "original", "v2");
            original = MirrorPulseAdapterRemoteBatchMapper.Map(root.InstanceId, [root], source);
            if (boundary == "captured")
                _ = await new MirrorPulseAdapterRemoteBatchProjector(catalog, store, router, new(catalog)).ProjectAsync(root.InstanceId, source);
            else
            {
                // Synthetic public progress tests migration/replay only, not native application.
                bool complete = boundary == "sdk-complete";
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.RemoteBatches.UpsertAsync(new(original.BatchId, complete ? original.FinalCursor.Span : original.InitialCursor.Span,
                    complete ? original.Changes.Count : 0, original.Changes.Count, complete ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying,
                    [], DateTimeOffset.UtcNow, original.Fingerprint, complete ? original.Changes[^1].ChangeId : null));
                await transaction.CommitAsync();
            }
            var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
            var start = await StartTransferAckAsync(catalog, target);
            var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
            var selected = await new MirrorPulseRemoteIdentityResolver(catalog,
                new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1))).ResolveAsync(root, "backend-file", "v3");
            Assert.AreNotEqual(original.Changes.Single().ItemId, selected.ItemId);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var official = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        var projector = new MirrorPulseAdapterRemoteBatchProjector(reopened, official, new(fixture.Paths.SyncRootPath, [root]), new(reopened));
        var replay = await projector.ProjectAsync(root.InstanceId, source);
        CollectionAssert.AreEqual(original.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
        var retained = (await reopened.ReadAdapterRemoteProjectionAsync(root.InstanceId, original.BatchId))!;
        _ = await projector.ProjectAsync(root.InstanceId, source);
        CollectionAssert.AreEqual(retained.Payload, (await reopened.ReadAdapterRemoteProjectionAsync(root.InstanceId, original.BatchId))!.Payload);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projector.ProjectAsync(root.InstanceId,
            GenerationWire(root, "original", "changed-wire-input")).AsTask());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdapterGenerationProjectionFreezesSuccessorBirthAndFollowingRevisionAcrossOwners(bool scoped)
    {
        using var fixture = new Fixture(); RootRegistration root; CloudRemoteChangeBatch original; AdapterRemoteChangeBatch source; string history;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        await using (var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath)))
        {
            var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); root = prepared.Root;
            var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
            var start = await StartTransferAckAsync(catalog, target);
            var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
            history = await ReadTransferRetirementBytesAsync(fixture.Paths);
            var first = GenerationWire(root, "successor", "v2");
            var next = first.Changes.Single() with { ChangeId = "next", RemoteRevision = "v3", PreviousRemoteRevision = "v2", CursorAfter = new byte[] { 3 } };
            source = new(first.BatchId, first.InitialCursor, [first.Changes.Single(), next], new byte[] { 3 });
            original = await new MirrorPulseAdapterRemoteBatchProjector(catalog, store, new(fixture.Paths.SyncRootPath, [root]),
                new(catalog, new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)))).ProjectAsync(root.InstanceId, source);
            Assert.AreNotEqual(prepared.Plan.Source.Identity.ItemId, original.Changes[0].ItemId);
            Assert.AreEqual(original.Changes[0].ItemId, original.Changes[1].ItemId);
            Assert.IsNull(original.Changes[0].PreviousRemoteRevision); Assert.AreEqual("v2", original.Changes[1].PreviousRemoteRevision);
            Assert.IsNotNull(await catalog.ReadAdapterRemoteProjectionAsync(root.InstanceId, original.BatchId));
            Assert.AreEqual(history, await ReadTransferRetirementBytesAsync(fixture.Paths));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var official = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root]);
        var projector = new MirrorPulseAdapterRemoteBatchProjector(reopened, official, router, new(reopened));
        var replay = await projector.ProjectAsync(root.InstanceId, source);
        CollectionAssert.AreEqual(original.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
        Assert.IsNull(await reopened.ReadRetainedRemoteBatchAsync(root.InstanceId), "A completed incoming slot is not the only projection witness.");
        var renamed = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
            "Renamed", "Renamed", root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
        router.ReplaceRegistrations([renamed]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projector.ProjectAsync(root.InstanceId, source).AsTask());
        Assert.AreEqual(history, await ReadTransferRetirementBytesAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdapterGenerationProjectionDeleteDoesNotAllocateSuccessorAndItsRetryCannotDeleteTheLaterGeneration(bool scoped)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); var root = prepared.Root;
        var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
        var start = await StartTransferAckAsync(catalog, target);
        var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)));
        var projector = new MirrorPulseAdapterRemoteBatchProjector(catalog, store, new(fixture.Paths.SyncRootPath, [root]), resolver);
        var wire = GenerationWire(root, "delete-original", "accepted");
        var deletion = new AdapterRemoteChangeBatch(wire.BatchId, wire.InitialCursor,
            [wire.Changes.Single() with { Kind = AdapterRemoteChangeKind.Delete }], wire.FinalCursor);
        var original = await projector.ProjectAsync(root.InstanceId, deletion);
        Assert.AreEqual(prepared.Plan.Source.Identity.ItemId, original.Changes.Single().ItemId);
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, prepared.Plan.Source.Identity.ItemId!.Value));
        var later = await projector.ProjectAsync(root.InstanceId, GenerationWire(root, "later", "v2"));
        Assert.AreNotEqual(original.Changes.Single().ItemId, later.Changes.Single().ItemId);
        var replay = await projector.ProjectAsync(root.InstanceId, deletion);
        CollectionAssert.AreEqual(original.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
        Assert.AreEqual(CloudRemoteChangeKind.Delete, replay.Changes.Single().Kind);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdapterGenerationProjectionReplaysAfterAnUnrelatedRootIsAdded(bool scoped)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); var root = prepared.Root;
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root]);
        var projector = new MirrorPulseAdapterRemoteBatchProjector(catalog, store, router, new(catalog));
        var source = GenerationWire(root, "original", "v2");
        var original = await projector.ProjectAsync(root.InstanceId, source);
        var unrelated = new RootRegistration(root.AdapterId, root.InstanceId, RootId.New(), "unrelated", "Unrelated", "Unrelated",
            false, RootRegistrationState.Active, root.RegisteredAt, RootIdentityScope.InstanceRoot);
        router.ReplaceRegistrations([root, unrelated]);
        var replay = await projector.ProjectAsync(root.InstanceId, source);
        CollectionAssert.AreEqual(original.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
    }

    private static AdapterRemoteChangeBatch GenerationWire(RootRegistration root, string batch, string revision) => new(batch, new byte[] { 1 },
        [new("change", AdapterRemoteChangeKind.FileUpsert, root.UniquenessKey, "backend-file", revision, AdapterRemoteItemKind.File,
            "new.txt", PreviousRemoteRevision: "accepted", Length: 4, Metadata: new AdapterRemoteMetadata(FileAttributes.Normal), CursorAfter: new byte[] { 2 })], new byte[] { 2 });
}
