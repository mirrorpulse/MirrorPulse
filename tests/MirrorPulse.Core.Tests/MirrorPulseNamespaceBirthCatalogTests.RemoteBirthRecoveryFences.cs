using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OnlyTheExactOriginalUnregisteredBirthCanResumeItsOwnDispatchFence()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog);
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteBirthAsync(birth));
        await catalog.PrepareNamespaceBirthAsync(birth);
        Assert.IsTrue(await catalog.CanResumeNamespaceRemoteBirthAsync(birth));
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteBirthAsync(birth with { RoleSid = "S-1-5-5-123-459" }));
        Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(birth.RootId));
        var pending = await catalog.ReadPendingNamespaceRemoteBirthsAsync(birth.RemoteEntry!.InstanceId, birth.RemoteEntry.BatchId);
        Assert.AreEqual(birth, pending.Single());
        await RegisterRemoteBornAsync(catalog, birth);
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteBirthAsync(birth));
        Assert.HasCount(0, await catalog.ReadPendingNamespaceRemoteBirthsAsync(birth.RemoteEntry.InstanceId, birth.RemoteEntry.BatchId));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnotherBirthInTheSameRootKeepsOriginalReplayClosedEvenWithAnotherBatchOwner(bool remoteSibling)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog); await catalog.PrepareNamespaceBirthAsync(birth);
        var sibling = birth with
        {
            OperationId = Guid.NewGuid(),
            RelativePath = "Docs/sibling",
            RemoteEntry = birth.RemoteEntry! with { BatchId = birth.RemoteEntry.BatchId + "/another", ItemId = Guid.NewGuid() }
        };
        if (!remoteSibling) sibling = sibling with { Version = 1, RemoteEntry = null };
        await catalog.PrepareNamespaceBirthAsync(sibling);
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteBirthAsync(birth));
        Assert.HasCount(1, await catalog.ReadPendingNamespaceRemoteBirthsAsync(birth.RemoteEntry!.InstanceId, birth.RemoteEntry.BatchId));
    }

    [TestMethod]
    public async Task AnIndependentRootBirthDoesNotBlockTheOriginalRootOwner()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog); await catalog.PrepareNamespaceBirthAsync(birth);
        var other = Baseline() with { RelativePath = "Other" }; var protection = Protection(other);
        await ProtectAsync(catalog, other, protection);
        await catalog.PrepareNamespaceBirthAsync(Birth(other, protection) with { RelativePath = "Other/child" });
        Assert.IsTrue(await catalog.CanResumeNamespaceRemoteBirthAsync(birth));
        var instance = InstanceId.New();
        Assert.HasCount(0, await catalog.ReadPendingNamespaceRemoteBirthsAsync(instance, instance + "/foreign"));
    }

    [TestMethod]
    public async Task PendingOwnerQueryAuthenticatesItsOriginalIndexBeforeGrantingReplay()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { birth = await PrepareRemoteBirthParentAsync(first); await first.PrepareNamespaceBirthAsync(birth); }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            second.ReadPendingNamespaceRemoteBirthsAsync(birth.RemoteEntry!.InstanceId, birth.RemoteEntry.BatchId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.CanResumeNamespaceRemoteBirthAsync(birth));
    }
}
