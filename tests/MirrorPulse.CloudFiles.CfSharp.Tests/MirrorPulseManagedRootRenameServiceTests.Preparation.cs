using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseManagedRootRenameServiceTests
{
    [TestMethod]
    public async Task RejectedPublicSdkCaptureDoesNotPublishAnUnwitnessedProductIntent()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (MirrorPulseRootRenameIntent intent in fixture.Intents)
            await fixture.Catalog.TransitionManagedRootRenameAsync(intent.OperationId,
                MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        var original = await fixture.Catalog.ReadManagedRootRenameHistoryAsync();
        var topology = await fixture.Catalog.ReadAdapterTopologyAsync();
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [fixture.Roots[0]]);
        var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, fixture.Catalog, router);

        // The SDK rejects preparation before its session starts, without a
        // native registration. No complete product preparation was accepted.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.PrepareAsync(fixture.Roots[0].RootId, "Rejected capture").AsTask());
        CollectionAssert.AreEqual(original.ToArray(), (await fixture.Catalog.ReadManagedRootRenameHistoryAsync()).ToArray());
        CollectionAssert.AreEqual(topology.Roots.ToArray(), (await fixture.Catalog.ReadAdapterTopologyAsync()).Roots.ToArray());
        Assert.IsFalse((await fixture.Catalog.ReadManagedRootRenamesAsync()).Any(intent => intent.IsPending));
    }
}
