using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterDeploymentHealthTests
{
    [TestMethod]
    [DataRow("HealthAtStartup", true)]
    [DataRow("HealthRecovers", true)]
    [DataRow("HealthInvalidField", false)]
    [DataRow("HealthWithoutCapability", false)]
    public async Task OnlyNegotiatedSignedHealthIsVisibleAndTheCurrentWorkerCanClearIt(string mode, bool accepted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = timeout.Token;
        string root = Directory.CreateTempSubdirectory("MirrorPulse-worker-health-").FullName;
        try
        {
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "state"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            InstalledAdapter package = AdapterConfigurationWorkerSessionTests.NewFixture("authorize");
            await catalog.AddInstallationAsync(package, token);
            var credentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            var values = new Dictionary<string, string> { ["endpoint"] = "https://example.test/" };
            var labels = new Dictionary<string, string>();
            AdapterInstance instance = await provisioner.CreateAsync(new(package.InstallId.ToString(), "Fixture", values, labels, null, true,
                new Dictionary<string, string?> { ["token"] = "health-fixture-token" }), token);
            await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-startup-fixture-mode"), mode, token);
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync(token);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentials);
            await supervisor.StartAsync(topology);
            string phase = mode switch
            {
                "HealthRecovers" => "Connected",
                "HealthAtStartup" => "Worker error",
                _ => "Worker failed",
            };
            MirrorPulseInstanceRuntimeState? runtime;
            do
            {
                runtime = await catalog.ReadInstanceRuntimeStateAsync(instance.InstanceId, token);
                if (runtime?.Phase == phase && (!accepted || supervisor.ReadReportedHealth(instance).Count > 0)) break;
                await Task.Delay(20, token);
            } while (true);

            if (!accepted)
            {
                Assert.IsEmpty(supervisor.ReadReportedHealth(instance));
                return;
            }

            IReadOnlyList<MirrorPulseAdapterHealthIssue> health = AdapterDeploymentHealth.Read(package, instance,
                supervisor.ReadReportedHealth(instance), "zh-CN");
            Assert.HasCount(1, health);
            Assert.AreEqual(new MirrorPulseAdapterHealthIssue(AdapterHealthReason.ExpiredCredential, "token", "Token"), health[0]);
            Assert.IsTrue(health[0].RequiresConfiguration);
            Assert.DoesNotContain("ignored-fixture-detail", JsonSerializer.Serialize(health));

            if (mode == "HealthAtStartup")
            {
                Assert.IsFalse(supervisor.IsConnected(instance.InstanceId));
                Assert.AreEqual("Unknown", runtime.LastErrorCode);
                AdapterInstance edited = await provisioner.ConfigureAsync(instance.InstanceId, "Edited", values, labels,
                    expectedRevision: instance.ConfigurationRevision, cancellationToken: token);
                Assert.IsEmpty(supervisor.ReadReportedHealth(edited));
            }
            else
            {
                Assert.IsTrue(supervisor.IsConnected(instance.InstanceId));
                await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-health-recovered"), "recovered", token);
                while (supervisor.ReadReportedHealth(instance).Count > 0) await Task.Delay(20, token);
                Assert.IsTrue(supervisor.IsConnected(instance.InstanceId));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void DetectsRequiredSettingsAndCredentialsWithoutAWorker()
    {
        InstalledAdapter package = AdapterConfigurationWorkerSessionTests.NewFixture("authorize");
        var oldDeployment = new AdapterInstance(package.AdapterId, package.InstallId, InstanceId.New(), "Existing",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), false,
            AdapterLifecycleState.Disabled, null, DateTimeOffset.UtcNow);

        var health = AdapterDeploymentHealth.Read(package, oldDeployment, [new(AdapterHealthReason.WorkerFailure)], "en-US");
        Assert.AreEqual(new MirrorPulseAdapterHealthIssue(AdapterHealthReason.MissingConfiguration, "endpoint", "Endpoint"), health[0]);
        Assert.AreEqual(new MirrorPulseAdapterHealthIssue(AdapterHealthReason.MissingCredential, "token", "Token"), health[1]);
        Assert.HasCount(2, health);
    }
}
