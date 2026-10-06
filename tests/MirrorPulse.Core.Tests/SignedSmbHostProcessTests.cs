using System.Text;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedSmbHostProcessTests
{
    [TestMethod]
    [TestCategory("OfficialPackages")]
    public async Task SignedSmbReleaseListsReadsAndConditionallyUploadsThroughHostAndCfSharp()
    {
        string? aggregateDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_OFFICIAL_AGGREGATE");
        string? share = Environment.GetEnvironmentVariable("MIRRORPULSE_SMB_TEST_SHARE");
        string? backingDirectory = Environment.GetEnvironmentVariable("MIRRORPULSE_SMB_TEST_BACKING");
        if (string.IsNullOrWhiteSpace(aggregateDirectory) || string.IsNullOrWhiteSpace(share) ||
            string.IsNullOrWhiteSpace(backingDirectory))
        {
            Assert.Inconclusive("Requires the OfficialPackages test environment; run the dedicated verification gate.");
        }

        string backingRoot = Path.GetFullPath(backingDirectory);
        string name = $"instance-{Guid.NewGuid():N}";
        string source = Path.GetFullPath(Path.Combine(backingRoot, name));
        string prefix = backingRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The SMB test directory escaped its backing root.");
        }

        string networkPath = Path.Combine(share, name);
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-signed-smb", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        byte[] original = Encoding.UTF8.GetBytes("signed SMB content");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "note.txt"), original);
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(aggregateDirectory, "official-adapters.manifest.json")));
            JsonElement smb = manifest.RootElement.EnumerateArray().Single(item =>
                item.GetProperty("adapterId").GetString() == "com.mirrorpulse.adapter.smb");
            string packageDirectory = Path.Combine(aggregateDirectory, "com.mirrorpulse.adapter.smb");
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installation = await catalog.InstallSignedAdapterAsync(
                Path.Combine(packageDirectory, smb.GetProperty("package").GetString()!),
                Path.Combine(packageDirectory, smb.GetProperty("signature").GetString()!),
                Path.Combine(root, "installed"), "win-x64");
            AdapterInstance instance = await catalog.CreateInstanceAsync(installation.InstallId,
                "SMB fixture", new Dictionary<string, string> { ["networkPath"] = networkPath },
                [], Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"));
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
            string rootKey = topology.Roots.Single(binding => binding.InstanceId == instance.InstanceId).UniquenessKey;
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog,
                new FixedCredentialStore("unused", string.Empty));
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
                    Assert.Fail($"The signed SMB Worker failed: {state.LastErrorCode}.");
                }

                await Task.Delay(50, timeout.Token);
            }

            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor,
                new MirrorPulseAdapterDirectoryPageSource(supervisor));
            CloudProviderDirectoryPage top = await provider.FetchChildrenAsync(paths.SyncRootPath,
                ReadOnlyMemory<byte>.Empty, null, timeout.Token);
            CloudPlaceholderSpec adapterRoot = top.Children.Single();
            CloudProviderDirectoryPage page = await provider.FetchChildrenAsync(
                Path.Combine(paths.SyncRootPath, adapterRoot.Name), adapterRoot.Identity.Encode(),
                null, timeout.Token);
            CloudFilePlaceholderSpec note = page.Children.OfType<CloudFilePlaceholderSpec>()
                .Single(item => item.Name == "note.txt");
            Assert.AreEqual(original.LongLength, note.Length);
            string filePath = Path.Combine(paths.SyncRootPath, adapterRoot.Name, note.Name);
            await using Stream read = await provider.OpenReadAsync(filePath, note.Identity.Encode(),
                note.Length, 1, 3, timeout.Token);
            read.Seek(1, SeekOrigin.Begin);
            byte[] range = new byte[3];
            await read.ReadExactlyAsync(range, timeout.Token);
            CollectionAssert.AreEqual(original.AsSpan(1, 3).ToArray(), range);

            string revision = (await supervisor.StatAsync(new MirrorPulseWorkerStatRequest(
                instance.InstanceId, "note.txt", rootKey), timeout.Token))!;
            Assert.AreEqual(note.Identity.RemoteRevision, revision);
            byte[] replacement = Encoding.UTF8.GetBytes("updated SMB content");
            await using (var content = new MemoryStream(replacement, writable: false))
            {
                string updated = await supervisor.UploadAsync(new MirrorPulseWorkerUploadRequest(
                    instance.InstanceId, "note.txt", revision, content, replacement.Length, RootKey: rootKey), timeout.Token);
                Assert.IsFalse(string.IsNullOrWhiteSpace(updated));
            }

            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(
                Path.Combine(source, "note.txt"), timeout.Token));
            await using (var stale = new MemoryStream([8, 8, 8], writable: false))
            {
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () => await supervisor.UploadAsync(
                    new MirrorPulseWorkerUploadRequest(instance.InstanceId, "note.txt", revision,
                        stale, 3, RootKey: rootKey), timeout.Token));
            }

            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(
                Path.Combine(source, "note.txt"), timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
        }
    }
}
