using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BirthProvenanceSurvivesReopenWithoutAdoptingTheCurrentPublication(bool preview7)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            birth = await PrepareRemoteBirthParentAsync(catalog);
            if (preview7) birth = birth with
            {
                RemoteEntry = birth.RemoteEntry! with
                { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit }
            };
            await catalog.PrepareNamespaceBirthAsync(birth);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(birth, await reopened.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(birth, await reopened.PrepareNamespaceBirthAsync(birth));
        Assert.IsNull(await reopened.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetirementProvenanceSurvivesReopenWithoutReauthorizingDeletion(bool preview7)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            intent = await RemoteRetirementAsync(catalog, setup.Member);
            if (preview7) intent = intent with
            {
                RemoteEntry = intent.RemoteEntry! with
                { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit }
            };
            await catalog.PrepareNamespaceRetirementAsync(intent);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.ReadNamespaceRetirementAsync(intent.OperationId);
        Assert.AreEqual(intent, retained!.Intent);
        Assert.IsNull(retained.StartedAt);
        Assert.AreEqual(retained, await reopened.PrepareNamespaceRetirementAsync(intent));
        Assert.IsNull(await reopened.ReadNamespaceRemoteRetirementOutcomeAsync(intent.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("birth", "0.1.0-preview.7", "3caa382e341d260543d3afc05e25fa034fb32e16")]
    [DataRow("birth", "0.1.0-preview.6", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    [DataRow("birth", "0.1.0-preview.8", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    [DataRow("retirement", "0.1.0-preview.7", "3caa382e341d260543d3afc05e25fa034fb32e16")]
    [DataRow("retirement", "0.1.0-preview.6", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    [DataRow("retirement", "0.1.0-preview.8", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    public async Task MixedOrUnknownPublicationsCannotAcquireProductProtocolEvidence(string boundary, string version, string source)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (boundary == "birth")
        {
            var birth = await PrepareRemoteBirthParentAsync(catalog);
            birth = birth with { RemoteEntry = birth.RemoteEntry! with { PackageVersion = version, PackageSource = source } };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBirthAsync(birth));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        }
        else
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            var intent = await RemoteRetirementAsync(catalog, setup.Member);
            intent = intent with { RemoteEntry = intent.RemoteEntry! with { PackageVersion = version, PackageSource = source } };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
            Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        }
    }
}
