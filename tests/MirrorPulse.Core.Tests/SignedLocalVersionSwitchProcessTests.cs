using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedLocalVersionSwitchProcessTests
{
    private static readonly string RuntimeIdentifier = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("The signed Worker test requires Windows x64 or ARM64."),
    };

    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task PinnedInstanceRunsEachSignedVersionAndStaysOfflineWhenDisabled()
    {
        string? aggregateDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        string? previousDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_LOCAL_PREVIOUS_RELEASE");
        if (string.IsNullOrWhiteSpace(aggregateDirectory) || string.IsNullOrWhiteSpace(previousDirectory))
        {
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");
        }

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(aggregateDirectory, "official-adapters.manifest.json")));
        JsonElement local = manifest.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("adapterId").GetString() == "com.mirrorpulse.adapter.local");
        string latestDirectory = Path.Combine(aggregateDirectory, "com.mirrorpulse.adapter.local");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-version-switch", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        byte[] content = Encoding.UTF8.GetBytes("version-switch-source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "note.txt"), content);
        try
        {
            InstanceId instanceId;
            InstalledAdapter previous;
            InstalledAdapter latest;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                previous = await catalog.InstallSignedAdapterAsync(
                    Path.Combine(previousDirectory, "com.mirrorpulse.adapter.local-0.1.3.mpadapter"),
                    Path.Combine(previousDirectory, "com.mirrorpulse.adapter.local-0.1.3.mpadapter.signature.json"),
                    Path.Combine(root, "installed"), RuntimeIdentifier);
                latest = await catalog.InstallSignedAdapterAsync(
                    Path.Combine(latestDirectory, local.GetProperty("package").GetString()!),
                    Path.Combine(latestDirectory, local.GetProperty("signature").GetString()!),
                    Path.Combine(root, "installed"), RuntimeIdentifier);
                Assert.AreNotEqual(previous.Version, latest.Version);
                AdapterInstance instance = await catalog.CreateInstanceAsync(previous.InstallId,
                    "Pinned Local", new Dictionary<string, string> { ["sourceDirectory"] = source },
                    [], Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"));
                instanceId = instance.InstanceId;
                await VerifyConnectedVersionAsync(catalog, instanceId, previous, content);

                await catalog.SelectInstanceInstallationAsync(instanceId, latest.InstallId);
                await catalog.SetInstanceEnabledAsync(instanceId, false);
            }

            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology offline = await catalog.ReadAdapterTopologyAsync();
                Assert.AreEqual(latest.InstallId, offline.Instances.Single().InstallId);
                await using (var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                    new WindowsCredentialManagerStore()))
                {
                    await supervisor.StartAsync(offline);
                    Assert.AreEqual("Offline", (await catalog.ReadInstanceRuntimeStateAsync(instanceId))?.Phase);
                    AdapterWorkerOperationException unavailable = await Assert.ThrowsExactlyAsync<AdapterWorkerOperationException>(async () =>
                        await supervisor.StatAsync(new MirrorPulseWorkerStatRequest(instanceId, "note.txt"),
                            CancellationToken.None));
                    Assert.AreEqual("Offline", unavailable.FailureCode);
                }

                await catalog.SetInstanceEnabledAsync(instanceId, true);
            }

            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await VerifyConnectedVersionAsync(catalog, instanceId, latest, content);
                MirrorPulseAdapterTopology selected = await catalog.ReadAdapterTopologyAsync();
                Assert.AreEqual(latest.InstallId, selected.Instances.Single().InstallId);
                Assert.IsTrue(selected.Instances.Single().Enabled);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyConnectedVersionAsync(
        MirrorPulseProductCatalog catalog,
        InstanceId instanceId,
        InstalledAdapter expected,
        byte[] content)
    {
        MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
        string rootKey = topology.Roots.Single(root => root.InstanceId == instanceId).UniquenessKey;
        AdapterInstanceWorkerPayload payload = AdapterInstanceWorkerLaunchResolver.Resolve(topology,
            instanceId, WorkerSessionId.New(), RuntimeIdentifier);
        Assert.AreEqual(expected.InstallId, payload.InstallId);
        Assert.AreEqual(expected.Version, payload.Version);
        string diagnostics = Path.Combine(topology.Instances.Single(item => item.InstanceId == instanceId).TransferCacheDirectory, "diagnostics");
        await using var supervisor = new AdapterInstanceProcessSupervisor(catalog,
            new WindowsCredentialManagerStore(), diagnosticsDirectory: diagnostics);
        await supervisor.StartAsync(topology);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(
                instanceId, timeout.Token);
            if (state?.Phase == "Connected")
            {
                break;
            }

            if (state?.Phase is "Worker failed" or "Worker error")
            {
                Assert.Fail($"The signed Local Worker failed: {state.LastErrorCode}. " +
                    await WorkerFailureTestDiagnostics.ReadAsync(diagnostics));
            }

            await Task.Delay(50, timeout.Token);
        }

        await WaitForProcessPathAsync(payload.LaunchRequest.ExecutablePath, timeout.Token);
        Assert.IsFalse(string.IsNullOrWhiteSpace(await supervisor.StatAsync(
            new MirrorPulseWorkerStatRequest(instanceId, "note.txt", rootKey), timeout.Token)));
        await using Stream read = await supervisor.ReadRangeAsync(new MirrorPulseWorkerReadRangeRequest(
            instanceId, "note.txt", ReadOnlyMemory<byte>.Empty, 0, content.Length, RootKey: rootKey), timeout.Token);
        byte[] actual = new byte[content.Length];
        await read.ReadExactlyAsync(actual, timeout.Token);
        CollectionAssert.AreEqual(content, actual);
    }

    private static async Task WaitForProcessPathAsync(string executablePath, CancellationToken cancellationToken)
    {
        var observed = new List<string>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (!process.ProcessName.Contains("MirrorPulse", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        observed.Add($"{process.ProcessName}: {process.MainModule?.FileName}");
                        if (string.Equals(process.MainModule?.FileName, executablePath,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                    catch (Win32Exception)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Expected signed Worker process at {executablePath}; observed " +
                    string.Join("; ", observed.Distinct()));
            }

            await Task.Delay(50, cancellationToken);
        }
    }
}
