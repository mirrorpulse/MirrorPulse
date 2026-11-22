using System.Text.Json;
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
    public async Task PollingDefersACompletedLocalRetirementWithoutReusingItsIdentityOrChangingOriginalSnapshots(bool scoped)
    {
        using var fixture = new Fixture();
        RootRegistration root;
        MirrorPulseNamespaceRetirement original;
        string snapshot;
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(fixture.Paths.DataRootPath);
        var source = new GenerationPollSource { Revision = "accepted" };
        int applied = 0;
        CloudRemoteChangeBatch? appliedBatch = null;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareCanonicalResolverSourceAsync(catalog, scoped);
            root = setup.Root;
            var instance = (await catalog.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
            var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
            await using var poller = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply,
                snapshotStore: snapshots, pendingStore: new MirrorPulseCatalogRemotePollPendingStore(catalog), identityResolver: resolver);
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
            snapshot = JsonSerializer.Serialize(await snapshots.LoadAsync(root));
            var member = setup.Plan.Source.Context.Member;
            var intent = new MirrorPulseNamespaceRetirementIntent(1, Guid.NewGuid(), member.MemberId, root.RootId,
                member.LocalObject, setup.Plan.Source.Context.Location.RelativePath, member.IsDirectory,
                member.OwnerSid, setup.Plan.Source.Context.Protection.RoleSid, setup.Plan.PreparedAt.AddSeconds(1));
            await catalog.PrepareNamespaceRetirementAsync(intent);
            await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, setup.Plan.Source.Identity.ItemId);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
            original = await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, intent.PreparedAt.AddSeconds(2));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId), "The real local-retirement fence must defer selection, without a new public address or a remote batch.");
            await AssertOriginalsAsync(catalog, resolver);
        }

        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var instance = (await catalog.ReadAdapterTopologyAsync()).Instances.Single(item => item.InstanceId == root.InstanceId);
            var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
            await using var restarted = new MirrorPulseActiveRemotePoller(source, [instance], [root], Apply,
                snapshotStore: snapshots, pendingStore: new MirrorPulseCatalogRemotePollPendingStore(catalog), identityResolver: resolver);
            Assert.IsFalse(await restarted.PollOnceAsync(root.InstanceId));
            await AssertOriginalsAsync(catalog, resolver);
            source.Present = false;
            Assert.IsTrue(await restarted.PollOnceAsync(root.InstanceId));
            Assert.IsEmpty((await snapshots.LoadAsync(root))!);
            Assert.AreEqual(original, await catalog.ReadNamespaceRetirementAsync(original.OperationId));
            Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.StartedItemId!.Value));
        }
        Assert.AreEqual(1, applied);
        Assert.IsNotNull(appliedBatch);
        var deletion = appliedBatch.Changes.Single();
        Assert.AreEqual(CloudRemoteChangeKind.Delete, deletion.Kind);
        Assert.AreEqual(original.StartedItemId, deletion.ItemId);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));

        async Task AssertOriginalsAsync(MirrorPulseProductCatalog catalog, MirrorPulseRemoteIdentityResolver resolver)
        {
            Assert.AreEqual(original, await catalog.ReadNamespaceRetirementAsync(original.OperationId));
            Assert.AreEqual(snapshot, JsonSerializer.Serialize(await snapshots.LoadAsync(root)));
            Assert.IsNull(await new MirrorPulseCatalogRemotePollPendingStore(catalog).LoadAsync(root, default));
            Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, original.StartedItemId!.Value));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resolver.ResolveAsync(root, "backend-file", "accepted").AsTask());
            var predecessor = await resolver.ReadCurrentAsync(root, "backend-file", "accepted");
            Assert.AreEqual(original.StartedItemId, predecessor.ItemId);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(root, predecessor).AsTask());
        }

        ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CancellationToken __)
        {
            applied++;
            appliedBatch = batch;
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }
    }
}
