using System.Text.Json;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedSftpHostProcessTests
{
    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task SignedSftpReleaseReadsAndConditionallyUploadsThroughHostAndCfSharp()
    {
        string? aggregateDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        if (string.IsNullOrWhiteSpace(aggregateDirectory))
        {
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");
        }

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(aggregateDirectory, "official-adapters.manifest.json")));
        JsonElement sftp = manifest.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("adapterId").GetString() == "com.mirrorpulse.adapter.sftp");
        string packageDirectory = Path.Combine(aggregateDirectory, "com.mirrorpulse.adapter.sftp");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-signed-sftp", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        await using SftpProtocolFixture fixture = await SftpProtocolFixture.StartAsync();
        byte[] original = [2, 5, 7, 11, 13];
        string file = Path.Combine(fixture.StorageDirectory, "report.bin");
        await File.WriteAllBytesAsync(file, original);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installation = await catalog.InstallSignedAdapterAsync(
                Path.Combine(packageDirectory, sftp.GetProperty("package").GetString()!),
                Path.Combine(packageDirectory, sftp.GetProperty("signature").GetString()!),
                Path.Combine(root, "installed"), "win-x64");
            AdapterInstance instance = await catalog.CreateInstanceAsync(installation.InstallId,
                "SFTP fixture", new Dictionary<string, string>
                {
                    ["endpoint"] = $"sftp://127.0.0.1:{fixture.Port}/",
                    ["username"] = "user",
                    ["credentialReference"] = "sftp-password",
                    ["trustedHostKeySha256"] = fixture.Fingerprint,
                }, ["sftp-password"], Path.Combine(root, "cache", "files"),
                Path.Combine(root, "cache", "transfers"));
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            RootRegistration registration = topology.Roots.Single(binding => binding.InstanceId == instance.InstanceId);
            string rootKey = registration.UniquenessKey;
            string diagnostics = Path.Combine(root, "diagnostics");
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                new FixedCredentialStore("sftp-password", "correct-secret"), diagnosticsDirectory: diagnostics);
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(
                    instance.InstanceId, timeout.Token);
                if (state?.Phase == "Connected")
                {
                    break;
                }

                if (state?.Phase is "Worker failed" or "Worker error")
                {
                    Assert.Fail($"The signed SFTP Worker failed: {state.LastErrorCode}. " +
                        await WorkerFailureTestDiagnostics.ReadAsync(diagnostics));
                }

                await Task.Delay(50, timeout.Token);
            }

            string revision = (await supervisor.StatAsync(new MirrorPulseWorkerStatRequest(
                instance.InstanceId, "report.bin", rootKey), timeout.Token))!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(revision));
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor,
                new MirrorPulseAdapterDirectoryPageSource(supervisor));
            string filePath = Path.Combine(paths.SyncRootPath, topology.Roots.Single().DirectoryName,
                "report.bin");
            byte[] identity = MirrorPulsePlaceholderIdentity.CreateForRoot(registration,
                "report.bin", revision).Encode();
            await using Stream read = await provider.OpenReadAsync(filePath, identity,
                original.Length, 2, 3, timeout.Token);
            read.Seek(2, SeekOrigin.Begin);
            byte[] range = new byte[3];
            await read.ReadExactlyAsync(range, timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 7, 11, 13 }, range);

            byte[] replacement = [1, 4, 9, 16];
            await using (var content = new MemoryStream(replacement, writable: false))
            {
                string updated = await supervisor.UploadAsync(new MirrorPulseWorkerUploadRequest(
                    instance.InstanceId, "report.bin", revision, content, replacement.Length, RootKey: rootKey), timeout.Token);
                Assert.IsFalse(string.IsNullOrWhiteSpace(updated));
            }

            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(file, timeout.Token));
            await using (var stale = new MemoryStream([8, 8, 8], writable: false))
            {
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () => await supervisor.UploadAsync(
                    new MirrorPulseWorkerUploadRequest(instance.InstanceId, "report.bin", revision,
                        stale, 3, RootKey: rootKey), timeout.Token));
            }

            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(file, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
