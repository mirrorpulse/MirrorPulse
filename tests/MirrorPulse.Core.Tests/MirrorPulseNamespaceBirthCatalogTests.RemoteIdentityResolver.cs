using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteIdentityResolverSelectsOneStableSuccessorAfterActualCatalogRetirementAcrossOwners(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; RootRegistration root; CloudPlaceholderIdentity successor; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalResolverSourceAsync(catalog, sameInstance); root = prepared.Root;
            var target = await PrepareSettledTransferAsync(catalog, sameInstance, originalPlan: prepared.Plan);
            var start = await StartTransferAckAsync(catalog, target);
            copy = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
            original = await ReadTransferRetirementBytesAsync(fixture.Paths);
            var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(copy.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)));
            successor = await resolver.ResolveAsync(root, "backend-file", "later-v2");
            Assert.AreNotEqual(copy.Plan.Source.Identity.ItemId, successor.ItemId);
            Assert.AreNotEqual(copy.Plan.Source.Identity.RemoteId, successor.RemoteId);
            var decoded = MirrorPulsePlaceholderIdentity.Decode(root.InstanceId, successor.Encode());
            Assert.AreEqual("backend-file", decoded.RemoteId); Assert.AreEqual("later-v2", decoded.RemoteRevision);
            Assert.AreEqual(root.RootId, decoded.StableRootId); Assert.IsNotNull(decoded.Generation);
            CollectionAssert.AreEqual(successor.Encode(), decoded.Encode());
            var edited = await resolver.ResolveAsync(root, "backend-file", "later-v3");
            Assert.AreEqual(successor.ItemId, edited.ItemId); Assert.AreEqual(successor.RemoteId, edited.RemoteId);
            Assert.AreEqual("later-v3", edited.RemoteRevision);
            Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
            Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var renamed = new RootRegistration(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey,
            "Renamed", "Renamed", root.CustomEntry, root.State, root.RegisteredAt, root.IdentityScope);
        var restored = await new MirrorPulseRemoteIdentityResolver(reopened).ResolveAsync(renamed, "backend-file", "later-v4");
        Assert.AreEqual(successor.ItemId, restored.ItemId); Assert.AreEqual(successor.RemoteId, restored.RemoteId);
        Assert.IsTrue(MirrorPulsePlaceholderIdentity.BelongsToRoot(renamed, restored));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RemoteIdentityResolverConcurrentCallbacksRetainOneSelectedGeneration()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, true);
        var target = await PrepareSettledTransferAsync(catalog, originalPlan: prepared.Plan);
        var start = await StartTransferAckAsync(catalog, target);
        var completed = await catalog.CompleteNamespaceTransferAcknowledgementAsync(start.Acknowledgement!, start.Acknowledgement!.StartedAt.AddSeconds(1));
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(completed.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1)));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => resolver.ResolveAsync(prepared.Root, "backend-file", "v2").AsTask()));
        Assert.AreEqual(1, results.Select(item => item.ItemId).Distinct().Count());
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM remote_identity_generation_history;"; Assert.AreEqual(1L, await count.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task RemoteIdentityResolverRejectsRetiredSourceBeforeOriginalAckAndKeepsItsFences()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, true);
        var target = await PrepareSettledTransferAsync(catalog, originalPlan: prepared.Plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MirrorPulseRemoteIdentityResolver(catalog)
            .ResolveAsync(prepared.Root, "backend-file", "v2").AsTask());
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(prepared.Root.RootId, prepared.Plan.Source.Identity.ItemId!.Value));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.Acknowledgement);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteIdentityResolverKeepsUnretiredIdentityWithoutSelectingFromRevisionChanges(bool sameInstance)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, sameInstance);
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
        var first = await resolver.ResolveAsync(prepared.Root, "backend-file", "v1");
        var edited = await resolver.ResolveAsync(prepared.Root, "backend-file", "v2");
        Assert.AreEqual(prepared.Plan.Source.Identity.ItemId, first.ItemId); Assert.AreEqual(first.ItemId, edited.ItemId);
        Assert.AreEqual(first.RemoteId, edited.RemoteId);
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(prepared.Root.RootId, first.ItemId));
    }

    [TestMethod]
    public void RemoteIdentityMapperKeepsLiteralGenerationLookingLegacyBackendAddresses()
    {
        var instance = InstanceId.New();
        string backend = "mp3:" + instance.Value.ToString("N") + ":literal-backend-address";
        var original = MirrorPulsePlaceholderIdentity.Create(instance, backend, "v1").ToCfSharp();
        var decoded = MirrorPulsePlaceholderIdentity.Decode(instance, original.Encode());
        Assert.AreEqual(backend, decoded.RemoteId); Assert.IsNull(decoded.Generation);
        CollectionAssert.AreEqual(original.Encode(), decoded.Encode());
        Assert.IsTrue(MirrorPulsePlaceholderIdentity.BelongsToInstance(instance, original));
    }

    private static async Task<(RootRegistration Root, MirrorPulseNamespaceTransferPlan Plan)> PrepareCanonicalResolverSourceAsync(
        MirrorPulseProductCatalog catalog, bool sameInstance)
    {
        var setup = await PrepareTransferFixtureAsync(catalog, sameInstance);
        var root = (await catalog.ReadAdapterTopologyAsync()).Roots.Single(item => item.RootId == setup.Plan.SourceRoute.RootId);
        var identity = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "backend-file", "accepted").ToCfSharp();
        var binding = new MirrorPulseUploadBinding(setup.Source.Member.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, "AQID");
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), root.InstanceId, root.UniquenessKey, MirrorPulseWorkerChangeKind.ContentUpdate,
            "new.txt", null, false, null, 4, new string('B', 64), MirrorPulseMutationOrigin.Journal, binding);
        await catalog.PrepareMutationAsync(intent);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
        var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, binding, identity.ItemId, identity.RemoteId, "accepted", 4, intent.ContentSha256!);
        var observed = setup.Plan.PreparedAt.AddSeconds(1);
        await catalog.SaveContentAcceptanceProofAsync(proof);
        await catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, new(MirrorPulseContentConfirmationOutcome.Confirmed,
            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete, true, true, true, true, 4, 1,
            0, 0, null, null, true, "BAUG", observed));
        await CorrelateAsync(catalog, setup.Source.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Source.Member.MemberId), intent.OperationId);
        var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Source.Member.MemberId))!;
        return (root, setup.Plan with { Source = current, SourceRemoteRevision = "accepted", PreparedAt = observed.AddSeconds(1) });
    }
}
