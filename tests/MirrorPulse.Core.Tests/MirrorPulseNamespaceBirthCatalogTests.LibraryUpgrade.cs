using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BirthProvenanceSurvivesReopenWithoutAdoptingTheCurrentPublication(bool currentPublication)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            birth = await PrepareRemoteBirthParentAsync(catalog);
            if (currentPublication) birth = birth with
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
    public async Task RetirementProvenanceSurvivesReopenWithoutReauthorizingDeletion(bool currentPublication)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            intent = await RemoteRetirementAsync(catalog, setup.Member);
            if (currentPublication) intent = intent with
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
    [DataRow("birth", "0.1.0-preview.8", "3caa382e341d260543d3afc05e25fa034fb32e16")]
    [DataRow("birth", "0.1.0-preview.7", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
    [DataRow("birth", "0.1.0-preview.6", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
    [DataRow("birth", "0.1.0-preview.9", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
    [DataRow("retirement", "0.1.0-preview.7", "3caa382e341d260543d3afc05e25fa034fb32e16")]
    [DataRow("retirement", "0.1.0-preview.6", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    [DataRow("retirement", "0.1.0-preview.8", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9")]
    [DataRow("retirement", "0.1.0-preview.8", "3caa382e341d260543d3afc05e25fa034fb32e16")]
    [DataRow("retirement", "0.1.0-preview.7", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
    [DataRow("retirement", "0.1.0-preview.6", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
    [DataRow("retirement", "0.1.0-preview.9", "ee593578dba3c666cf59f4a922f0e220f115bf7f")]
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Preview7ProtocolHistoryRemainsOriginalAfterPreview8Admission(bool retirement)
    {
        const string historicalVersion = "0.1.0-preview.7";
        const string historicalSource = "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9";
        using var fixture = new Fixture();
        MirrorPulseNamespaceBirthIntent? birth = null;
        MirrorPulseNamespaceRetirementIntent? intent = null;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            if (retirement)
            {
                var setup = await PrepareMovableMemberAsync(catalog);
                intent = await RemoteRetirementAsync(catalog, setup.Member);
                intent = intent with { RemoteEntry = intent.RemoteEntry! with { PackageVersion = historicalVersion, PackageSource = historicalSource } };
                await catalog.PrepareNamespaceRetirementAsync(intent);
            }
            else
            {
                birth = await PrepareRemoteBirthParentAsync(catalog);
                birth = birth with { RemoteEntry = birth.RemoteEntry! with { PackageVersion = historicalVersion, PackageSource = historicalSource } };
                await catalog.PrepareNamespaceBirthAsync(birth);
            }
        }

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (retirement)
        {
            var original = intent ?? throw new InvalidOperationException("The original retirement was not prepared.");
            var retained = await reopened.ReadNamespaceRetirementAsync(original.OperationId);
            Assert.AreEqual(original, retained!.Intent);
            Assert.IsNull(retained.StartedAt);
            Assert.AreEqual(retained, await reopened.PrepareNamespaceRetirementAsync(original));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceRetirementAsync(original with
            {
                RemoteEntry = original.RemoteEntry! with
                { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit }
            }));
            Assert.AreEqual(retained, await reopened.ReadNamespaceRetirementAsync(original.OperationId));
            Assert.IsNull(await reopened.ReadNamespaceRemoteRetirementOutcomeAsync(original.OperationId));
        }
        else
        {
            var original = birth ?? throw new InvalidOperationException("The original birth was not prepared.");
            Assert.AreEqual(original, await reopened.ReadNamespaceBirthAsync(original.OperationId));
            Assert.AreEqual(original, await reopened.PrepareNamespaceBirthAsync(original));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceBirthAsync(original with
            {
                RemoteEntry = original.RemoteEntry! with
                { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit }
            }));
            Assert.AreEqual(original, await reopened.ReadNamespaceBirthAsync(original.OperationId));
            Assert.IsNull(await reopened.ReadNamespaceBirthStartAsync(original.OperationId));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
