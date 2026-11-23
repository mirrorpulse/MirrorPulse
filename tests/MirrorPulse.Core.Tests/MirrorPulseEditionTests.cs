using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseEditionTests
{
    private static readonly Guid StandardProviderId = new("d194eacd-c38c-49df-a238-49871f9c8d1b");
    private static readonly Guid CustomProviderId = new("7e2494ee-f27a-44cd-a564-15dc2eee476e");

    [TestMethod]
    public void StandardBuildKeepsExistingRuntimeIdentity()
    {
        Assert.AreEqual("standard", ProductInfo.Edition.Id);
        Assert.AreEqual("MirrorPulse", ProductInfo.Edition.IdentityPrefix);
        Assert.AreEqual("MIRRORPULSE_", ProductInfo.Edition.EnvironmentPrefix);
        Assert.AreEqual(StandardProviderId, ProductInfo.Edition.ProviderId);
    }

    [TestMethod]
    public void CustomDisplayNameDoesNotChangeTheEditionNamespace()
    {
        var edition = new MirrorPulseEdition("sample-drive", "Sample drive", CustomProviderId);
        var renamed = new MirrorPulseEdition("sample-drive", "Renamed drive", CustomProviderId);

        Assert.AreEqual("MirrorPulse.sample-drive", edition.IdentityPrefix);
        Assert.AreEqual("MIRRORPULSE_EDITION_SAMPLE_DRIVE_", edition.EnvironmentPrefix);
        Assert.AreEqual(edition.IdentityPrefix, renamed.IdentityPrefix);
        Assert.AreEqual(edition.ProviderId, renamed.ProviderId);
        Assert.AreNotEqual(edition.DisplayName, renamed.DisplayName);
        Assert.AreNotEqual(edition.IdentityPrefix, ProductInfo.Edition.IdentityPrefix);
    }

    [TestMethod]
    public void EditionsUseSeparateWinUiStatusPipesForTheSameUser()
    {
        const string sid = "S-1-5-21-100-200-300-1001";
        var custom = new MirrorPulseEdition("sample-drive", "Sample Drive", CustomProviderId);

        Assert.AreEqual("MirrorPulse-app-" + sid, MirrorPulseAppStatusPipe.GetPipeName(ProductInfo.Edition, sid));
        Assert.AreEqual("MirrorPulse.sample-drive-app-" + sid, MirrorPulseAppStatusPipe.GetPipeName(custom, sid));
    }

    [TestMethod]
    public void EditionRejectsAProviderOrIdentifierThatCouldShareStandardState()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MirrorPulseEdition("sample-drive", "Sample drive", StandardProviderId));
        Assert.ThrowsExactly<ArgumentException>(() => new MirrorPulseEdition("standard", "MirrorPulse", CustomProviderId));
        Assert.ThrowsExactly<ArgumentException>(() => new MirrorPulseEdition("sample_drive", "Sample drive", CustomProviderId));
        Assert.ThrowsExactly<ArgumentException>(() => new MirrorPulseEdition("../sample-drive", "Sample drive", CustomProviderId));
    }
}
