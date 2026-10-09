using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class WorkerStartupDiagnosticsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("ExitBeforePipe", "AwaitPipe", true, false, true)]
    [DataRow("IdleBeforePipe", "AwaitPipe", true, false, false)]
    [DataRow("IdleBeforeHello", "AwaitHello", true, true, false)]
    [DataRow("UnexpectedHello", "Negotiate", false, true, false)]
    [DataRow("RejectedHello", "Negotiate", false, true, false)]
    [DataRow("InvalidExecutable", "StartProcess", false, false, false)]
    public async Task RealWorkerFailureRetainsStageAndPreTerminationProcessFacts(
        string mode, string stage, bool deadline, bool connected, bool exited)
    {
        await RunFixtureAsync(mode, async (catalog, instanceId, supervisor, diagnostics, stop) =>
        {
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            await supervisor.StartAsync(topology);
            if (mode == "ExitBeforePipe")
                await File.WriteAllTextAsync(Path.Combine(topology.Instances.Single().TransferCacheDirectory, ".mp-startup-fixture-exit"), "exit");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            MirrorPulseInstanceRuntimeState? state;
            do
            {
                state = await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token);
                if (state?.Phase is "Worker failed" or "Worker error") break;
                await Task.Delay(25, timeout.Token);
            } while (true);
            string retained = await WorkerFailureTestDiagnostics.ReadAsync(diagnostics);
            TestContext.WriteLine("WorkerStartupObservation: " + retained);
            SafeLogEntry entry = JsonSerializer.Deserialize<SafeLogEntry[]>(retained)!.Single();
            Assert.AreEqual("WorkerSessionFailed", entry.Code);
            Assert.AreEqual(stage, entry.Fields["workerStage"]);
            Assert.AreEqual(deadline.ToString(), entry.Fields["workerDeadlineExpired"]);
            Assert.AreEqual(connected.ToString(), entry.Fields["workerPipeConnected"]);
            bool started = mode != "InvalidExecutable";
            Assert.AreEqual(started.ToString(), entry.Fields["workerProcessStarted"]);
            Assert.AreEqual(started.ToString(), entry.Fields["workerProcessStateObserved"]);
            if (started) Assert.AreEqual(exited.ToString(), entry.Fields["workerProcessHasExited"]);
            else Assert.IsFalse(entry.Fields.ContainsKey("workerProcessHasExited"));
            if (exited) Assert.AreEqual("73", entry.Fields["workerProcessExitCode"]);
            else Assert.IsFalse(entry.Fields.ContainsKey("workerProcessExitCode"));
            if (mode == "InvalidExecutable") Assert.IsTrue(entry.Fields["workerNativeErrorCode"] is "193" or "216",
                "Windows must report bad executable format or executable machine type mismatch.");
            else Assert.IsFalse(entry.Fields.ContainsKey("workerNativeErrorCode"));
            Assert.AreEqual(instanceId.ToString(), entry.Fields["instanceId"]);
            Assert.IsTrue(Guid.TryParse(entry.Fields["workerSessionId"], out _));
            long elapsed = long.Parse(entry.Fields["workerStageElapsedMs"], CultureInfo.InvariantCulture);
            Assert.IsGreaterThanOrEqualTo(0L, elapsed);
            if (deadline) Assert.IsGreaterThanOrEqualTo(connected ? 4000L : 14000L, elapsed);
            Assert.IsFalse(retained.Contains("needle", StringComparison.Ordinal));
            Assert.IsFalse(retained.Contains(diagnostics, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(deadline ? "OperationCanceledException" : mode == "InvalidExecutable" ? "Win32Exception" :
                mode == "RejectedHello" ? "ProtocolVersionUnsupported" : "InvalidDataException",
                state!.LastErrorCode);
        });
    }

    [TestMethod]
    public async Task RequestedShutdownDrainsWorkerWithoutAFalseFailureObservation()
    {
        await RunFixtureAsync("IdleBeforePipe", async (catalog, instanceId, supervisor, diagnostics, stop) =>
        {
            await supervisor.StartAsync(await catalog.ReadAdapterTopologyAsync());
            Assert.AreEqual("Starting", (await catalog.ReadInstanceRuntimeStateAsync(instanceId))?.Phase);
            await stop();
            Assert.IsFalse(File.Exists(Path.Combine(diagnostics, "mirrorpulse.log")));
            Assert.IsNull((await catalog.ReadInstanceRuntimeStateAsync(instanceId))?.LastErrorCode);
        });
    }

    private static async Task RunFixtureAsync(string mode,
        Func<MirrorPulseProductCatalog, InstanceId, AdapterInstanceProcessSupervisor, string, Func<Task>, Task> verify)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-worker-startup-tests")) + Path.DirectorySeparatorChar;
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "transfers");
        string diagnostics = Path.Combine(directory, "diagnostics");
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(directory, ".mp-worker-startup-fixture"), "synthetic");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cache, ".mp-startup-fixture-mode"), mode);
            string executable = Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe");
            if (mode == "InvalidExecutable")
            {
                executable = Path.Combine(directory, "invalid-worker.exe");
                await File.WriteAllTextAsync(executable, "startup-secret-needle");
            }
            AdapterId adapter = AdapterId.Parse("example.startupfixture");
            InstanceId instanceId = InstanceId.New();
            InstallId installId = InstallId.New();
            var manifest = new AdapterManifest(1, adapter, "Fixture", "1.0.0", new(1, mode == "RejectedHello" ? 1 : 2),
                new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
            var installed = new InstalledAdapter(manifest, installId, Path.GetDirectoryName(executable)!,
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            var instance = new AdapterInstance(adapter, installId, instanceId, "Fixture", new Dictionary<string, string>(), [],
                Path.Combine(directory, "files"), cache, true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(new(Path.Combine(directory, "sync"), Path.Combine(directory, "data")));
            await catalog.SaveAdapterTopologyAsync(new([installed], [instance], []));
            var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore(), diagnosticsDirectory: diagnostics);
            bool disposed = false;
            async Task StopAsync()
            {
                if (disposed) return;
                await supervisor.DisposeAsync();
                disposed = true;
            }
            try { await verify(catalog, instanceId, supervisor, diagnostics, StopAsync); }
            finally { await StopAsync(); }
        }
        finally { DeleteMarkedFixture(prefix, directory); }
    }

    private static void DeleteMarkedFixture(string prefix, string directory)
    {
        if (!Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            !File.Exists(Path.Combine(directory, ".mp-worker-startup-fixture")))
            throw new InvalidOperationException("The synthetic startup fixture scope is invalid.");
        Directory.Delete(directory, recursive: true);
    }
}
