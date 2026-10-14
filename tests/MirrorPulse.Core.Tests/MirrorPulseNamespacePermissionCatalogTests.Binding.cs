using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionCatalogTests
{
    [TestMethod]
    public async Task BindingLookupRetainsTheOriginalAcrossRestartAndDoesNotAdoptTheSamePath()
    {
        using var fixture = new CatalogFixture(); var baseline = Baseline();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await CompleteAsync(catalog, baseline, Intent(baseline));
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineByBindingAsync(baseline.LocalObject));
        foreach (var other in new[] { baseline.LocalObject with { VolumeSerialNumber = 2 },
            baseline.LocalObject with { SyncRootFileId = Guid.NewGuid() }, baseline.LocalObject with { LocalFileId = Guid.NewGuid() } })
            Assert.IsNull(await reopened.ReadNamespacePermissionBaselineByBindingAsync(other));
        Assert.HasCount(1, await reopened.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task BindingLookupRejectsChangedObjectIndexesRatherThanReturningAnotherOriginal()
    {
        using var fixture = new CatalogFixture(); var baseline = Baseline();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await CompleteAsync(catalog, baseline, Intent(baseline));
        Guid replacement = Guid.NewGuid();
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_permission_baselines SET local_file_id='{replacement:D}';");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionBaselineByBindingAsync(
            baseline.LocalObject with { LocalFileId = replacement }));
    }

    [TestMethod]
    public async Task BindingLookupRejectsInvalidIdentityAndCancellationBeforeReading()
    {
        using var fixture = new CatalogFixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var binding = Baseline().LocalObject;
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ReadNamespacePermissionBaselineByBindingAsync(binding with { SyncRootFileId = Guid.Empty }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespacePermissionBaselineByBindingAsync(binding, new(true)));
    }
}
