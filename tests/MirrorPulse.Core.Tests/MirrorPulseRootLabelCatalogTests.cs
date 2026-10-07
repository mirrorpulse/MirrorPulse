using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCatalogTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelChangePersistsWithoutReplacingIdentitySourceSettingsOrOtherRoots(bool enabled)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-label-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.labels"), "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
            [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        RootRegistration original;
        AdapterInstance instance;
        string source = Path.Combine(root, "source");
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "source remains unchanged");
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.AddInstallationAsync(installation);
                instance = await catalog.CreateInstanceAsync(installation.InstallId, "Storage", new Dictionary<string, string> { ["sourceDirectory"] = source },
                    [], Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"), enabled);
                original = (await catalog.ReadAdapterTopologyAsync()).Roots.Single(item => item.UniquenessKey == "docs");
                RootRegistration renamed = await catalog.RenameManagedRootAsync(original.RootId, "My Files");
                Assert.AreEqual("My Files", renamed.Label);
                Assert.AreEqual(original.RootId, renamed.RootId);
                Assert.AreEqual(original.State, renamed.State);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RenameManagedRootAsync(original.RootId, "archive"));
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RenameManagedRootAsync(original.RootId, "../outside"));
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RenameManagedRootAsync(RootId.New(), "Unknown"));
            }
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
                RootRegistration renamed = topology.Roots.Single(item => item.RootId == original.RootId);
                Assert.AreEqual("My Files", renamed.DirectoryName);
                Assert.AreEqual(original.InstanceId, renamed.InstanceId);
                Assert.AreEqual(original.AdapterId, renamed.AdapterId);
                Assert.AreEqual(original.UniquenessKey, renamed.UniquenessKey);
                Assert.AreEqual(original.RegisteredAt, renamed.RegisteredAt);
                Assert.AreEqual(original.IdentityScope, renamed.IdentityScope);
                Assert.AreEqual("Archive", topology.Roots.Single(item => item.UniquenessKey == "archive").Label);
                AdapterInstance retained = topology.Instances.Single();
                Assert.AreEqual(instance.InstanceId, retained.InstanceId);
                Assert.AreEqual(instance.InstallId, retained.InstallId);
                Assert.AreEqual(instance.Enabled, retained.Enabled);
                Assert.AreEqual(source, retained.Configuration["sourceDirectory"]);
                Assert.AreEqual("source remains unchanged", await File.ReadAllTextAsync(Path.Combine(source, "keep.txt")));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "My Files")));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
