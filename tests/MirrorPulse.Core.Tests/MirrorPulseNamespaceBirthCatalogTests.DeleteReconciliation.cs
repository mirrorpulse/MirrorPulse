using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(1, "0.1.0-preview.11", "c28895203e20d588aa3d0eb1bb4c0e55c25896fb")]
    [DataRow(1, "0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06")]
    [DataRow(2, "0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06")]
    public async Task PreservingDeleteRetainsItsOriginalReconciliationModeAndPublicationAcrossOwners(int version, string package, string source)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteRejectionAdmission original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = (await RejectionAdmissionAsync(first)) with { Version = version, PackageVersion = package, PackageSource = source };
            await first.PrepareRemoteDeleteRejectionAsync(original);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, (await second.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal))!.Admission);
        if (package == "0.1.0-preview.11")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => second.PrepareRemoteDeleteRejectionAsync(original with { Version = 2 }));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.PrepareRemoteDeleteRejectionAsync(original with { Version = version == 1 ? 2 : 1 }));
        Assert.AreEqual(original, (await second.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal))!.Admission);
        Assert.IsNull(await second.ReadNamespaceRemoteRetirementAsync(original.InstanceId, original.BatchId, original.Ordinal));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(2, "0.1.0-preview.11", "c28895203e20d588aa3d0eb1bb4c0e55c25896fb")]
    [DataRow(2, "0.1.0-preview.12", "c28895203e20d588aa3d0eb1bb4c0e55c25896fb")]
    [DataRow(2, "0.1.0-preview.11", "cfbc7f83b26b5b6515710a8c490428d2989d8c06")]
    [DataRow(3, "0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06")]
    public async Task PreservingDeleteCannotBorrowANewReconciliationModeFromAnotherPublication(int version, string package, string source)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = (await RejectionAdmissionAsync(catalog)) with { Version = version, PackageVersion = package, PackageSource = source };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareRemoteDeleteRejectionAsync(original));
        Assert.IsNull(await catalog.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal));
        Assert.IsEmpty(await catalog.ReadPendingRemoteDeleteRejectionsAsync(original.RootId));
    }
}
