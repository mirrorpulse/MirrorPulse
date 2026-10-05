using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedTemplateWorkerProcessTests
{
    [TestMethod]
    [TestCategory("TemplatePackage")]
    public async Task SignedV2TemplateInstallsAndRunsThroughProductionSupervisor()
    {
        string? package = Environment.GetEnvironmentVariable("MP_TEMPLATE_PACKAGE");
        string? publicKey = Environment.GetEnvironmentVariable("MP_TEMPLATE_PUBLIC_KEY");
        if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(publicKey))
            Assert.Inconclusive("Requires the independent template package gate.");
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-template-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        try
        {
            using RSA publisher = RSA.Create();
            publisher.ImportFromPem(await File.ReadAllTextAsync(publicKey!));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher, "MirrorPulse Dry Run");
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(2, installed.Manifest.Protocol.Minimum);
            Assert.AreEqual(2, installed.Manifest.Protocol.Maximum);
            InstanceId instanceId = InstanceId.New();
            var instance = new AdapterInstance(installed.AdapterId, installed.InstallId, instanceId, "Memory",
                new Dictionary<string, string>(), [], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
                true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            RootRegistration Root(string key) => new(installed.AdapterId, instanceId, RootId.New(), key,
                key, key, false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var topology = new MirrorPulseAdapterTopology([installed], [instance], [Root("left"), Root("right")]);
            await catalog.SaveAdapterTopologyAsync(topology);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token))?.Phase != "Connected")
                await Task.Delay(25, timeout.Token);
            foreach (string root in new[] { "left", "right" })
            {
                MirrorPulseWorkerDirectoryPage page = await supervisor.ReadDirectoryPageAsync(new(instanceId, "", ReadOnlyMemory<byte>.Empty, 1, root), timeout.Token);
                Assert.HasCount(1, page.Entries);
                string expected = "Memory source: " + root + "\n";
                await using Stream content = await supervisor.ReadRangeAsync(new(instanceId, "readme.txt", ReadOnlyMemory<byte>.Empty, 0,
                    System.Text.Encoding.UTF8.GetByteCount(expected), root), timeout.Token);
                using var reader = new StreamReader(content);
                Assert.AreEqual(expected, await reader.ReadToEndAsync(timeout.Token));
            }
            using var upload = new MemoryStream(new byte[] { 4, 5, 6 });
            string revision = await supervisor.UploadAsync(new(instanceId, "upload.bin", null, upload, upload.Length, Guid.NewGuid(), RootKey: "left"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "upload.bin", "right"), timeout.Token));
            Guid operation = Guid.NewGuid();
            var move = new MirrorPulseWorkerMoveRequest(instanceId, "upload.bin", "moved.bin", revision, false, operation, "left", "right");
            Assert.AreEqual(revision, await supervisor.MoveAsync(move, timeout.Token));
            Assert.AreEqual(revision, await supervisor.MoveAsync(move, timeout.Token));
            Assert.AreEqual("directory", await supervisor.CreateDirectoryAsync(new(instanceId, "left", "empty", Guid.NewGuid()), timeout.Token));
            await supervisor.DeleteAsync(new(instanceId, "moved.bin", revision, false, Guid.NewGuid(), "right"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "moved.bin", "right"), timeout.Token));
            Assert.IsEmpty(Directory.EnumerateFiles(instance.TransferCacheDirectory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
