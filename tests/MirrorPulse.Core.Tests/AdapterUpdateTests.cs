using System.Runtime.InteropServices;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterUpdateTests
{
    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task AcceptedWorkerOfferAcquiresOfficialPackageWithoutSwitchingDeployment()
    {
        const string adapterId = "com.mirrorpulse.adapter.local";
        string? aggregate = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        if (string.IsNullOrWhiteSpace(aggregate))
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(aggregate, "official-adapters.manifest.json"), token));
        JsonElement local = manifest.RootElement.EnumerateArray().Single(item => item.GetProperty("adapterId").GetString() == adapterId);
        string package = Path.Combine(aggregate, adapterId, local.GetProperty("package").GetString()!);
        string version = local.GetProperty("version").GetString()!;
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string root = Directory.CreateTempSubdirectory("MirrorPulse-official-update-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "state"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            InstalledAdapter previous = AdapterConfigurationWorkerSessionTests.NewFixture("authorize", adapterId);
            await catalog.AddInstallationAsync(previous, token);
            var credentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            AdapterInstance instance = await provisioner.CreateAsync(new(previous.InstallId.ToString(), "Fixture",
                new Dictionary<string, string> { ["endpoint"] = "https://example.test/" },
                new Dictionary<string, string>(), null, true,
                new Dictionary<string, string?> { ["token"] = "update-fixture-token" }), token);
            await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-startup-fixture-mode"), "UpdateOfficialLocal", token);
            await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-update-offer.json"),
                JsonSerializer.Serialize(new { offer = new { adapterId, version, location = new Uri(package).AbsoluteUri } }), token);
            MirrorPulseAdapterTopology original = await catalog.ReadAdapterTopologyAsync(token);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentials);
            await supervisor.StartAsync(original);
            while (!supervisor.IsConnected(instance.InstanceId)) await Task.Delay(20, token);
            var updates = new AdapterUpdateCoordinator(catalog, supervisor, paths.DataRootPath, runtime);

            AdapterUpdateCheck offer = await updates.CheckAsync(instance.InstanceId, token);
            Assert.AreEqual(AdapterUpdateAvailability.Available, offer.Availability);
            Assert.AreEqual(version, offer.AvailableVersion);
            Assert.IsNotNull(offer.OfferId);
            Assert.HasCount(1, (await catalog.ReadAdapterTopologyAsync(token)).Installations);
            AdapterUpdateInstallation result = await updates.InstallAsync(offer.OfferId.Value, token);
            MirrorPulseAdapterTopology current = await catalog.ReadAdapterTopologyAsync(token);
            Assert.HasCount(2, current.Installations);
            InstalledAdapter installed = current.Installations.Single(item => item.InstallId.ToString() == result.InstallId);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(version, result.Version);
            Assert.AreEqual(adapterId, installed.AdapterId.Value);
            Assert.AreEqual(local.GetProperty("packageSha256").GetString(), installed.PackageSha256.Hexadecimal, ignoreCase: true);
            Assert.AreEqual(previous.InstallId, current.Instances.Single().InstallId);
            Assert.AreEqual(original.Roots.Single().RootId, current.Roots.Single().RootId);
            Assert.IsTrue(supervisor.IsConnected(instance.InstanceId));
            Assert.AreEqual(AdapterUpdateAvailability.AlreadyInstalled, (await updates.CheckAsync(instance.InstanceId, token)).Availability);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => updates.InstallAsync(offer.OfferId.Value, token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
