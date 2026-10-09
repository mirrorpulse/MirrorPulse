using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseSyncRootRegistrationTests
{
    [TestMethod]
    public void RegistrationServiceBuildsMirrorPulseIdentityAndUpdatesExistingRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var registrar = new RecordingRegistrar();
        var service = new MirrorPulseSyncRootRegistrationService(registrar);
        var providerId = Guid.Parse("7b6a7d8e-63a5-4b8a-9c6e-1e6e61f95e9d");

        var result = service.Register(new MirrorPulseSyncRootDefinition(path, "0.1.0", providerId, [1, 2, 3]));

        Assert.AreEqual(Path.GetFullPath(path), result.Path);
        Assert.AreEqual(providerId, registrar.Options!.ProviderId);
        Assert.AreEqual("MirrorPulse", registrar.Options.ProviderName);
        Assert.IsTrue(registrar.Options.UpdateExisting);
        Assert.IsTrue(registrar.Options.MarkRootInSync);
        Assert.AreEqual(CloudHydrationPolicy.Full, registrar.Options.HydrationPolicy);
        Assert.AreEqual(CloudHydrationPolicyModifiers.None, registrar.Options.HydrationModifiers);
        Assert.AreEqual(CloudInSyncPolicy.None, registrar.Options.InSyncPolicy);
        Assert.AreEqual(CloudHardLinkPolicy.Disallowed, registrar.Options.HardLinkPolicy);
        Assert.AreEqual(CloudInSyncPolicy.TrackAll, SyncRootRegistrationOptions.CreateBuilder("Other", "1.0").Build().InSyncPolicy);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, registrar.Options.SyncRootIdentity.ToArray());
        Assert.IsTrue(Directory.Exists(path));
        Directory.Delete(path, recursive: true);
    }

    private sealed class RecordingRegistrar : IMirrorPulseSyncRootRegistrar
    {
        public SyncRootRegistrationOptions? Options { get; private set; }

        public MirrorPulseSyncRootRegistrationResult Register(string path, SyncRootRegistrationOptions options)
        {
            Options = options;
            return new(path, options.ProviderId, options.ProviderName, options.ProviderVersion);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.19041")]
    public async Task NativeOwnedRootPolicyMigrationPreservesFilesAndIdentity()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string path = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var definition = new MirrorPulseSyncRootDefinition(path, "0.1.0", Guid.NewGuid(), [1, 2, 3]);
        try
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "preserved.txt"), "preserved content");
            CloudSyncRoot.Register(path, SyncRootRegistrationOptions.CreateBuilder("MirrorPulse", "0.1.0")
                .WithProviderId(definition.ProviderId).WithSyncRootIdentity(definition.Identity).AllowHardLinks(true).Build());
            CloudSyncRootInfo previous = CloudSyncRoot.Open(path).GetInfo();
            Assert.AreEqual(CloudInSyncPolicy.TrackAll, previous.InSyncPolicy);
            Assert.AreEqual(CloudHardLinkPolicy.Allowed, previous.HardLinkPolicy);
            registry.EnsureCompatible(definition);
            registry.Register(definition);
            CloudSyncRootInfo migrated = CloudSyncRoot.Open(path).GetInfo();
            Assert.AreEqual(CloudInSyncPolicy.None, migrated.InSyncPolicy);
            Assert.AreEqual(CloudHardLinkPolicy.Disallowed, migrated.HardLinkPolicy);
            Assert.AreEqual(previous.FileId, migrated.FileId);
            CollectionAssert.AreEqual(previous.SyncRootIdentity.ToArray(), migrated.SyncRootIdentity.ToArray());
            Assert.AreEqual("preserved content", await File.ReadAllTextAsync(Path.Combine(path, "preserved.txt")));
            Assert.ThrowsExactly<InvalidDataException>(() => registry.EnsureCompatible(
                new(path, "0.1.0", definition.ProviderId, [4, 5, 6])));
        }
        finally
        {
            registry.Unregister(path);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
