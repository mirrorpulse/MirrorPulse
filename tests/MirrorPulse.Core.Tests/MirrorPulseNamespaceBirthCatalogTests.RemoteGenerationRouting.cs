using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteGenerationRoutingSharesSelectedIdentityAcrossPopulationHydrationAndCurrentLookup(bool scoped)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); var root = prepared.Root;
        var target = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
        var start = await StartTransferAckAsync(catalog, target);
        var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
        string history = await ReadTransferRetirementBytesAsync(fixture.Paths);
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)));
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root], identityResolver: resolver);
        var transport = new GenerationRangeTransport(); var pages = new GenerationPollSource { Revision = "v2" };
        var provider = new MirrorPulseDemandProvider(router, transport, pages);
        var original = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "backend-file", "accepted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.OpenReadAsync(root.DirectoryName + "/new.txt", original.Encode(), 4, 0, 4).AsTask());
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.ItemId), "A stale callback cannot allocate the later generation.");
        var selected = await router.CreateCurrentFileIdentityAsync(root.InstanceId, root.UniquenessKey, "backend-file", "v2");
        var children = await provider.FetchChildrenAsync(root.DirectoryName, router.CreateRootPage().Children.Single().Identity.Encode(), null);
        CollectionAssert.AreEqual(selected.Encode(), children.Children.Single().Identity.Encode());
        await using (var read = await provider.OpenReadAsync(root.DirectoryName + "/new.txt", selected.Encode(), 4, 0, 4))
        {
            byte[] bytes = new byte[4]; Assert.AreEqual(4, await read.ReadAsync(bytes));
            CollectionAssert.AreEqual(GenerationRangeTransport.Bytes, bytes);
        }
        Assert.HasCount(1, transport.Requests);
        CollectionAssert.AreEqual(selected.Encode(), transport.Requests.Single().FileIdentity.ToArray());
        Assert.AreEqual(root.UniquenessKey, transport.Requests.Single().RootKey);
        Assert.AreEqual("new.txt", transport.Requests.Single().NormalizedPath);
        Assert.IsTrue(MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, "backend-file", selected));
        Assert.IsTrue(MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, "backend-file", original));
        Assert.IsFalse(MirrorPulsePlaceholderIdentity.IdentifiesBackendObject(root, "different-backend", selected));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.OpenReadAsync(root.DirectoryName + "/new.txt", original.Encode(), 4, 0, 4).AsTask());
        var renamed = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
            "Renamed", "Renamed", root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
        router.ReplaceRegistrations([renamed]);
        var edited = await router.CreateCurrentFileIdentityAsync(root.InstanceId, root.UniquenessKey, "backend-file", "v3");
        Assert.AreEqual(selected.ItemId, edited.ItemId); Assert.AreEqual(selected.RemoteId, edited.RemoteId);
        Assert.AreEqual(root.InstanceId, router.Resolve("Renamed/new.txt", edited.Encode()).InstanceId);
        var disabled = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
            "Renamed", "Renamed", root.CustomEntry, RootRegistrationState.Disabled, root.RegisteredAt, root.IdentityScope);
        router.ReplaceRegistrations([disabled]);
        await Assert.ThrowsExactlyAsync<IOException>(() => router.CreateCurrentFileIdentityAsync(root.InstanceId, root.UniquenessKey, "backend-file", "v4").AsTask());
        Assert.HasCount(1, transport.Requests); Assert.AreEqual(history, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteGenerationRoutingRejectsUnacknowledgedRetirementAndIgnoresDeletedEnumerationEntries(bool scoped)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, scoped); var root = prepared.Root;
        _ = await PrepareSettledTransferAsync(catalog, scoped, originalPlan: prepared.Plan);
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root], identityResolver: new(catalog));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => router.CreateCurrentFileIdentityAsync(root.InstanceId,
            root.UniquenessKey, "backend-file", "later").AsTask());
        var entries = new[] { new CloudRemoteDirectoryEntry("backend-file", "later", CloudItemKind.File, "new.txt", null, 4,
            CloudPlaceholderMetadata.CreateFileBuilder().Build(), true) };
        var plan = await MirrorPulsePlaceholderBatchCoordinator.PlanAsync(router, root.InstanceId, root.UniquenessKey, entries);
        Assert.IsEmpty(plan.Placeholders); Assert.HasCount(1, plan.IgnoredEntries);
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, prepared.Plan.Source.Identity.ItemId!.Value));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    private sealed class GenerationRangeTransport : IMirrorPulseWorkerRangeTransport
    {
        internal static readonly byte[] Bytes = [9, 8, 7, 6];
        internal List<MirrorPulseWorkerReadRangeRequest> Requests { get; } = [];
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            Requests.Add(request); return ValueTask.FromResult<Stream>(new MemoryStream(Bytes, writable: false));
        }
    }
}
