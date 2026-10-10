using System.Runtime.Versioning;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using Windows.Storage.Provider;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseShellSyncRootRegistrarTests
{
    [TestMethod]
    public void ProfileUsesOneStableCurrentUserRootIdentity()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", "shell-root");
        var providerId = Guid.Parse("89f1747b-62aa-48bd-a725-e33f20c271a5");
        var definition = new MirrorPulseSyncRootDefinition(rootPath, "0.1.0", providerId, [1, 2, 3]);

        var first = MirrorPulseShellSyncRootRegistrar.CreateProfile(definition, "S-1-5-21-123");
        var second = MirrorPulseShellSyncRootRegistrar.CreateProfile(definition, "S-1-5-21-123");

        Assert.AreEqual("MirrorPulse!S-1-5-21-123!Default", first.RegistrationId);
        Assert.AreEqual(first.RegistrationId, second.RegistrationId);
        Assert.AreEqual(rootPath, first.SyncRootPath);
        Assert.AreEqual(providerId, first.ProviderId);
        Assert.AreEqual("MirrorPulse", first.DisplayName);
        Assert.AreEqual("0.1.0", first.ProviderVersion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.IconResource));
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("PackagedShell")]
    public async Task PackagedRegistrationPublishesCustomThenUnifiedDisplayName()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_TEST") != "1")
        {
            Assert.Inconclusive("Requires the PackagedShell test environment; run the dedicated verification gate.");
        }

        Assert.AreEqual("0B72358D-6DC9-479D-8C28-F0232B42A0B3",
            Windows.ApplicationModel.Package.Current.Id.Name);

        string rootPath = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var definition = new MirrorPulseSyncRootDefinition(rootPath, "0.1.0",
            Guid.Parse("89f1747b-62aa-48bd-a725-e33f20c271a5"), [1, 2, 3]);
        MirrorPulseShellRegistrationProfile custom = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(
            definition, "Personal drive");
        MirrorPulseShellRegistrationProfile unified = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(
            definition, "MirrorPulse");
        try
        {
            await MirrorPulseShellSyncRootRegistrar.RegisterAsync(custom);
            StorageProviderSyncRootInfo customInfo =
                StorageProviderSyncRootManager.GetSyncRootInformationForId(custom.RegistrationId);
            Assert.AreEqual("Personal drive", customInfo.DisplayNameResource);
            Assert.AreEqual(StorageProviderHydrationPolicy.Full, customInfo.HydrationPolicy);
            Assert.AreEqual(StorageProviderHydrationPolicyModifier.None, customInfo.HydrationPolicyModifier);
            Assert.AreEqual(StorageProviderPopulationPolicy.Full, customInfo.PopulationPolicy);
            Assert.AreEqual(MirrorPulseShellSyncRootRegistrar.ContentInSyncPolicy, customInfo.InSyncPolicy);

            await MirrorPulseShellSyncRootRegistrar.RegisterAsync(unified);
            StorageProviderSyncRootInfo unifiedInfo =
                StorageProviderSyncRootManager.GetSyncRootInformationForId(unified.RegistrationId);
            Assert.AreEqual("MirrorPulse", unifiedInfo.DisplayNameResource);
            Assert.AreEqual(StorageProviderPopulationPolicy.Full, unifiedInfo.PopulationPolicy);
            Assert.AreEqual(MirrorPulseShellSyncRootRegistrar.ContentInSyncPolicy, unifiedInfo.InSyncPolicy);
        }
        finally
        {
            MirrorPulseShellSyncRootRegistrar.Unregister(unified);
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
