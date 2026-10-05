using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterWorkerV2SessionTests
{
    [TestMethod]
    public async Task RealWorkerProcessNegotiatesTwoRootsWithTheProductionSupervisor()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        string executable = Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe");
        AdapterId adapter = AdapterId.Parse("example.protocolfixture");
        InstanceId instanceId = InstanceId.New();
        InstallId installId = InstallId.New();
        var manifest = new AdapterManifest(1, adapter, "Fixture", "1.0.0", new(1, 2),
            new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
        var installed = new InstalledAdapter(manifest, installId, Path.GetDirectoryName(executable)!,
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(adapter, installId, instanceId, "Fixture", new Dictionary<string, string>
        {
            ["root.left.sourcePath"] = "left-source",
            ["root.right.sourcePath"] = "right-source",
        }, [], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        RootRegistration Root(string key) => new(adapter, instanceId, RootId.New(), key, key, key, false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
        var topology = new MirrorPulseAdapterTopology([installed], [instance], [Root("left"), Root("right")]);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(new(Path.Combine(directory, "sync"), Path.Combine(directory, "data")));
            await catalog.SaveAdapterTopologyAsync(topology);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token))?.Phase != "Connected")
                await Task.Delay(25, timeout.Token);
            foreach (string key in new[] { "left", "right" })
            {
                Assert.AreEqual(key + "/revision", await supervisor.StatAsync(new(instanceId, "same.txt", key), timeout.Token));
                MirrorPulseWorkerDirectoryPage page = await supervisor.ReadDirectoryPageAsync(new(instanceId, "", ReadOnlyMemory<byte>.Empty, 4, key), timeout.Token);
                Assert.IsTrue(page.IsComplete);
                await using Stream content = await supervisor.ReadRangeAsync(new(instanceId, "same.txt", ReadOnlyMemory<byte>.Empty, 0, 4, key), timeout.Token);
                using var reader = new StreamReader(content);
                Assert.AreEqual(key == "left" ? "left" : "rght", await reader.ReadToEndAsync(timeout.Token));
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => supervisor.StatAsync(new(instanceId, "same.txt", "unconfigured"), timeout.Token).AsTask());
            Assert.AreEqual("directory", await supervisor.CreateDirectoryAsync(new(instanceId, "left", "new", Guid.NewGuid()), timeout.Token));
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "new", "right"), timeout.Token));
            using var upload = new MemoryStream(new byte[] { 4, 5, 6 });
            string uploaded = await supervisor.UploadAsync(new(instanceId, "upload.txt", null, upload, upload.Length, Guid.NewGuid(), RootKey: "left"), timeout.Token);
            Assert.AreEqual(uploaded, await supervisor.StatAsync(new(instanceId, "upload.txt", "left"), timeout.Token));
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "upload.txt", "right"), timeout.Token));
            Guid stableMoveId = Guid.NewGuid();
            var move = new MirrorPulseWorkerMoveRequest(instanceId, "upload.txt", "moved.txt", uploaded, false, stableMoveId, "left", "right");
            Assert.AreEqual(uploaded, await supervisor.MoveAsync(move, timeout.Token));
            Assert.AreEqual(uploaded, await supervisor.MoveAsync(move, timeout.Token), "A retried intent keeps its stable operation ID.");
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "upload.txt", "left"), timeout.Token));
            Assert.AreEqual(uploaded, await supervisor.StatAsync(new(instanceId, "moved.txt", "right"), timeout.Token));
            await supervisor.DeleteAsync(new(instanceId, "moved.txt", uploaded, false, Guid.NewGuid(), "right"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "moved.txt", "right"), timeout.Token));
            Assert.AreEqual("left/revision", await supervisor.StatAsync(new(instanceId, "same.txt", "left"), timeout.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void LegacyWorkerCannotNegotiateMultipleRootsOrExceedItsSignedManifest()
    {
        AdapterId adapter = AdapterId.Parse("example.fixture");
        InstanceId instance = InstanceId.New();
        RootRegistration Root(string key) => new(adapter, instance, RootId.New(), key, key, key, false, RootRegistrationState.Disabled, DateTimeOffset.UtcNow);
        RootRegistration[] roots = [Root("left"), Root("right")];
        using JsonDocument legacy = JsonDocument.Parse("{}");
        Assert.AreEqual(1, AdapterWorkerProtocolSession.Negotiate(legacy.RootElement, roots[..1], new(1, 1)).ProtocolVersion);
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() => AdapterWorkerProtocolSession.Negotiate(legacy.RootElement, roots, new(1, 1)));
        Assert.AreEqual("MultipleRootsRequireProtocolV2", error.Message);
        using JsonDocument offer = JsonDocument.Parse("{\"supportedVersions\":{\"minimum\":2,\"maximum\":2},\"capabilities\":[]}");
        Assert.AreEqual("ProtocolVersionUnsupported", Assert.ThrowsExactly<InvalidDataException>(() =>
            AdapterWorkerProtocolSession.Negotiate(offer.RootElement, roots, new(1, 1))).Message);
    }
}
