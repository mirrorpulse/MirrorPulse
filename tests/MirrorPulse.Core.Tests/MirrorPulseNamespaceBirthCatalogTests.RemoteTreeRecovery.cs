using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OnlyTheOriginalSdkTreeCanResumeItsFenceAcrossCatalogOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteTreeAsync(first, 2);
            original = await first.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
                setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
            Assert.IsTrue(await first.CanResumeNamespaceRemoteRetirementTreeAsync(original));
            CollectionAssert.Contains((await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), original.RootId);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var remote = original.RemoteEntry!;
        var pending = await second.ReadPendingNamespaceRemoteRetirementTreesAsync(remote.InstanceId, remote.BatchId);
        Assert.HasCount(1, pending); Assert.AreEqual(original.OperationId, pending[0].OperationId);
        Assert.AreEqual(remote, pending[0].RemoteEntry); CollectionAssert.AreEqual(original.Entries.ToArray(), pending[0].Entries.ToArray());
        Assert.IsTrue(await second.CanResumeNamespaceRemoteRetirementTreeAsync(pending[0]));
        var otherInstance = InstanceId.New();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => second.ReadPendingNamespaceRemoteRetirementTreesAsync(otherInstance, remote.BatchId));
        Assert.HasCount(0, await second.ReadPendingNamespaceRemoteRetirementTreesAsync(otherInstance, otherInstance + "/unknown"));
        Assert.HasCount(0, await second.ReadPendingNamespaceRemoteRetirementTreesAsync(remote.InstanceId, remote.BatchId + "-other"));
        CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), original.RootId);
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("batch")]
    [DataRow("fingerprint")]
    [DataRow("tree")]
    [DataRow("member")]
    [DataRow("phase")]
    public async Task ChangedSdkTreeCannotBorrowAnOriginalRecoveryFence(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 1);
        var original = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
            setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        var candidate = changed switch
        {
            "operation" => original with { OperationId = Guid.NewGuid() },
            "batch" => original with { RemoteEntry = original.RemoteEntry! with { BatchId = original.RemoteEntry.BatchId + "-changed" } },
            "fingerprint" => original with { RemoteEntry = original.RemoteEntry! with { BatchFingerprint = new string('B', 64) } },
            "tree" => original with { Entries = original.Entries.Reverse().ToArray() },
            "member" => original with { MemberId = Guid.NewGuid() },
            _ => original with { Phase = MirrorPulseNamespaceRetirementBatchPhase.Completed }
        };
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(candidate));
        Assert.IsTrue(await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), original.RootId);
    }

    [TestMethod]
    public async Task OriginalSdkTreeRecoveryReadsRejectALostManifestOwner()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 1);
        var original = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
            setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_remote_retirement_tree_owners SET change_id='changed';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceRemoteRetirementTreesAsync(
            original.RemoteEntry!.InstanceId, original.RemoteEntry.BatchId));
    }
}
