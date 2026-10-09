using System.Text.Json;
using MirrorPulse.Adapter.Ftp.Worker;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterInstanceProcessSupervisorTests
{
    [TestMethod]
    public async Task EnabledInstanceStartsIndependentWorkerAndDisabledInstanceStaysOffline()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        string executable = Path.ChangeExtension(typeof(FtpWorkerEntryMarker).Assembly.Location, ".exe");
        AdapterId adapterId = AdapterId.Parse("example.drive");
        InstallId installId = InstallId.New();
        InstanceId enabledId = InstanceId.New();
        InstanceId disabledId = InstanceId.New();
        var manifest = new AdapterManifest(1, adapterId, "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string>
            {
                ["win-x64"] = Path.GetFileName(executable),
                ["win-arm64"] = Path.GetFileName(executable),
            }, new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0");
        var installation = new InstalledAdapter(manifest, installId, Path.GetDirectoryName(executable)!,
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null,
            true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var invalidConfiguration = new Dictionary<string, string>
        {
            ["endpoint"] = "https://user:secret@example.test/",
            ["username"] = "user",
            ["credentialReference"] = "ftp-password",
            ["securityMode"] = "ExplicitTls",
        };
        AdapterInstance MakeInstance(InstanceId id, bool enabled) => new(adapterId, installId, id,
            enabled ? "Enabled" : "Disabled", invalidConfiguration, ["ftp-password"],
            Path.Combine(root, id.ToString(), "files"), Path.Combine(root, id.ToString(), "transfers"),
            enabled, enabled ? AdapterLifecycleState.Enabled : AdapterLifecycleState.Disabled,
            null, DateTimeOffset.UtcNow);
        var topology = new MirrorPulseAdapterTopology([installation],
            [MakeInstance(enabledId, true), MakeInstance(disabledId, false)], []);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.SaveAdapterTopologyAsync(topology);
            await using (var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                new WindowsCredentialManagerStore(), diagnosticsDirectory: Path.Combine(root, "diagnostics")))
            {
                await supervisor.StartAsync(topology);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(
                        enabledId, timeout.Token);
                    if (state?.Phase == "Worker error" && state.LastErrorCode == "InvalidConfiguration")
                    {
                        break;
                    }

                    await Task.Delay(50, timeout.Token);
                }

                MirrorPulseInstanceRuntimeState? offline = await catalog.ReadInstanceRuntimeStateAsync(disabledId);
                Assert.AreEqual("Offline", offline?.Phase);
                string retained = await WorkerFailureTestDiagnostics.ReadAsync(Path.Combine(root, "diagnostics"));
                SafeLogEntry entry = JsonSerializer.Deserialize<SafeLogEntry[]>(retained)!.Single();
                Assert.AreEqual("ReceiveFrames", entry.Fields["workerStage"]);
                Assert.AreEqual("True", entry.Fields["workerProcessStarted"]);
                Assert.AreEqual("True", entry.Fields["workerPipeConnected"]);
                Assert.AreEqual("False", entry.Fields["workerDeadlineExpired"]);
                Assert.AreEqual("InvalidConfiguration", entry.Fields["workerFailureCode"]);
                Assert.IsFalse(retained.Contains("secret", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
