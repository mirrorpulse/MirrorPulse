using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class WorkerConnectionReadinessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("Journal failed")]
    [DataRow("Transferring")]
    [DataRow("Reconciling")]
    public async Task ActualReadyConnectionSurvivesIndependentCatalogStatusAndClosesAfterDrain(string phase)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string executable = Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe");
            var adapter = AdapterId.Parse("example.connection-readiness"); var instanceId = InstanceId.New(); var installId = InstallId.New();
            var manifest = new AdapterManifest(1, adapter, "Fixture", "1.0.0", new(1, 2),
                new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
            var installed = new InstalledAdapter(manifest, installId, Path.GetDirectoryName(executable)!,
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            var instance = new AdapterInstance(adapter, installId, instanceId, "Fixture", new Dictionary<string, string>
            { ["root.left.sourcePath"] = "left-source" }, [], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
                true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            var root = new RootRegistration(adapter, instanceId, RootId.New(), "left", "Left", "Left", false,
                RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var topology = new MirrorPulseAdapterTopology([installed], [instance], [root]);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(new(Path.Combine(directory, "sync"), Path.Combine(directory, "data")));
            await catalog.SaveAdapterTopologyAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            try
            {
                Assert.IsFalse(supervisor.IsConnected(instanceId));
                await supervisor.StartAsync(topology);
                while (!supervisor.IsConnected(instanceId)) await Task.Delay(25, timeout.Token);
                await catalog.SaveInstanceRuntimeStateAsync(new(instanceId, phase, false, null, "RetainedDiagnostic"), timeout.Token);
                Assert.AreEqual("left/revision", await supervisor.StatAsync(new(instanceId, "same.txt", "left"), timeout.Token));
                Assert.IsTrue(supervisor.IsConnected(instanceId)); Assert.IsFalse(supervisor.IsConnected(InstanceId.New()));
                var retained = await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token);
                Assert.AreEqual(phase, retained!.Phase); Assert.AreEqual("RetainedDiagnostic", retained.LastErrorCode);
            }
            finally { await supervisor.DisposeAsync(); }
            Assert.IsFalse(supervisor.IsConnected(instanceId));
            await Assert.ThrowsExactlyAsync<AdapterWorkerOperationException>(() => supervisor.StatAsync(new(instanceId, "same.txt", "left"), timeout.Token).AsTask());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void OlderStatusPayloadDoesNotInventConnectionReadinessFromItsPhase()
    {
        var older = MirrorPulseControlJsonCodec.Deserialize<MirrorPulseAppInstanceStatus>("""
            {"instanceId":"fixture","displayName":"Fixture","enabled":true,"phase":"Connected","cursorFingerprint":null,"cursorUpdatedAt":null,"lastSuccessfulSync":null}
            """u8);
        Assert.IsNull(older.WorkerConnected);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VersionOneStatusPreservesConnectionReadinessAlongsideAnIndependentFailure(bool connected)
    {
        var status = new MirrorPulseAppInstanceStatus("fixture", "Fixture", true, "Journal failed", null, null, null,
            "RetainedDiagnostic")
        { WorkerConnected = connected };
        var roundTrip = JsonSerializer.Deserialize<MirrorPulseAppInstanceStatus>(JsonSerializer.Serialize(status, JsonOptions), JsonOptions)!;
        Assert.AreEqual<bool?>(connected, roundTrip.WorkerConnected); Assert.AreEqual("Journal failed", roundTrip.Phase);
        Assert.AreEqual("RetainedDiagnostic", roundTrip.LastErrorCode);
    }
}
