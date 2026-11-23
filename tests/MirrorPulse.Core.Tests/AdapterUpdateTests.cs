using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterUpdateTests
{
    [TestMethod]
    [DataRow("UpdateCurrent", AdapterUpdateAvailability.Current)]
    [DataRow("UpdateUnsupported", AdapterUpdateAvailability.Unsupported)]
    [DataRow("UpdateOtherAdapter", AdapterUpdateAvailability.Current)]
    [DataRow("UpdateAvailable", AdapterUpdateAvailability.Available)]
    [DataRow("UpdateLocal", AdapterUpdateAvailability.Available)]
    [DataRow("UpdateDelayed", AdapterUpdateAvailability.Available)]
    public async Task UpdateDiscoveryUsesTheSelectedWorkerAndLeavesTheDeploymentPinned(string mode, AdapterUpdateAvailability expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string root = Directory.CreateTempSubdirectory("MirrorPulse-worker-update-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "state"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            InstalledAdapter package = AdapterConfigurationWorkerSessionTests.NewFixture("authorize");
            await catalog.AddInstallationAsync(package, token);
            var credentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            AdapterInstance instance = await provisioner.CreateAsync(new(package.InstallId.ToString(), "Fixture",
                new Dictionary<string, string> { ["endpoint"] = "https://example.test/" }, new Dictionary<string, string>(),
                null, true, new Dictionary<string, string?> { ["token"] = "update-fixture-token" }), token);
            await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-startup-fixture-mode"), mode, token);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentials);
            var updates = new AdapterUpdateCoordinator(catalog, supervisor, paths.DataRootPath, "win-x64");
            Assert.AreEqual(AdapterUpdateAvailability.Offline, (await updates.CheckAsync(instance.InstanceId, token)).Availability);
            await supervisor.StartAsync(await catalog.ReadAdapterTopologyAsync(token));
            while (!supervisor.IsConnected(instance.InstanceId)) await Task.Delay(20, token);

            if (mode == "UpdateOtherAdapter")
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updates.CheckAsync(instance.InstanceId, token));
            }
            else
            {
                if (mode == "UpdateDelayed")
                {
                    using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(token);
                    Task<AdapterUpdateCheck> pending = updates.CheckAsync(instance.InstanceId, abandoned.Token);
                    while (!File.Exists(Path.Combine(instance.TransferCacheDirectory, ".mp-update-in-flight")))
                        await Task.Delay(20, token);
                    await abandoned.CancelAsync();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                    await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-update-reply"), "reply", token);
                }

                AdapterUpdateCheck result = await updates.CheckAsync(instance.InstanceId, token);
                Assert.AreEqual(expected, result.Availability);
                Assert.AreEqual(package.Version, result.CurrentVersion);
                if (expected == AdapterUpdateAvailability.Available)
                {
                    Assert.AreEqual("1.0.1", result.AvailableVersion);
                    Assert.IsNotNull(result.OfferId);
                    Assert.AreEqual(mode == "UpdateLocal" ? Path.Combine(instance.TransferCacheDirectory, "next.mpadapter")
                        : "https://example.invalid/next.mpadapter", result.Source);
                }
                else Assert.IsNull(result.OfferId);
            }

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => updates.InstallAsync(Guid.NewGuid(), token));
            MirrorPulseAdapterTopology current = await catalog.ReadAdapterTopologyAsync(token);
            Assert.AreEqual(package.InstallId, current.Instances.Single().InstallId);
            Assert.HasCount(1, current.Installations);
            Assert.IsTrue(supervisor.IsConnected(instance.InstanceId));
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.DataRootPath, "adapters", "downloads")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
