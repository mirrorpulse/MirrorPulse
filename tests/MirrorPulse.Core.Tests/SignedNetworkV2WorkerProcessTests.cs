using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CfSharp;
using MirrorPulse.Adapter.Ftp.Worker;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedNetworkV2WorkerProcessTests
{
    private static readonly string[] EnabledCredentials = ["left-credential", "right-credential"];

    [TestMethod]
    [TestCategory("FtpV2Package")]
    public async Task SignedFtpV2BindsTlsReadsAndRefusedMutationsThroughProductionHost()
    {
        RequireCandidate("FTP");
        byte[] left = [1, 2, 3, 4, 5];
        byte[] right = [9, 8, 7, 6, 5];
        await using var leftSource = new FtpWorkerProcessTests.LoopbackFtpFixture(
            FtpSecurityMode.ExplicitTls, "user-left", "secret-left", left);
        await using var rightSource = new FtpWorkerProcessTests.LoopbackFtpFixture(
            FtpSecurityMode.ImplicitTls, "user-right", "secret-right", right);
        var configuration = new Dictionary<string, string>();
        foreach (string key in new[] { "left", "right" })
        {
            var source = key == "left" ? leftSource : rightSource;
            configuration["root." + key + ".endpoint"] = $"ftp://127.0.0.1:{source.Port}/";
            configuration["root." + key + ".username"] = "user-" + key;
            configuration["root." + key + ".credentialReference"] = key + "-credential";
            configuration["root." + key + ".securityMode"] = key == "left" ? "ExplicitTls" : "ImplicitTls";
            configuration["root." + key + ".trustedCertificateSha256"] = source.CertificateSha256;
        }
        await VerifyHostAsync("ftp", configuration, left, right);
        CollectionAssert.AreEqual(left, leftSource.ReadStoredFile("/report.bin"));
        CollectionAssert.AreEqual(right, rightSource.ReadStoredFile("/report.bin"));
        Assert.IsTrue(leftSource.Authenticated && rightSource.Authenticated);
        Assert.IsTrue(leftSource.ControlChannelEncrypted && rightSource.ControlChannelEncrypted);
    }

    [TestMethod]
    [TestCategory("SftpV2Package")]
    public async Task SignedSftpV2BindsPinnedKeysReadsAndRefusedMutationsThroughProductionHost()
    {
        RequireCandidate("SFTP");
        byte[] left = [1, 2, 3, 4, 5];
        byte[] right = [9, 8, 7, 6, 5];
        await using SftpProtocolFixture leftSource = await SftpProtocolFixture.StartAsync("left");
        await using SftpProtocolFixture rightSource = await SftpProtocolFixture.StartAsync("right");
        var configuration = new Dictionary<string, string>();
        foreach (string key in new[] { "left", "right" })
        {
            SftpProtocolFixture source = key == "left" ? leftSource : rightSource;
            await File.WriteAllBytesAsync(Path.Combine(source.StorageDirectory, "report.bin"), key == "left" ? left : right);
            configuration["root." + key + ".endpoint"] = $"sftp://127.0.0.1:{source.Port}/";
            configuration["root." + key + ".username"] = "user-" + key;
            configuration["root." + key + ".credentialReference"] = key + "-credential";
            configuration["root." + key + ".trustedHostKeySha256"] = source.Fingerprint;
        }
        await VerifyHostAsync("sftp", configuration, left, right);
        CollectionAssert.AreEqual(left, await File.ReadAllBytesAsync(Path.Combine(leftSource.StorageDirectory, "report.bin")));
        CollectionAssert.AreEqual(right, await File.ReadAllBytesAsync(Path.Combine(rightSource.StorageDirectory, "report.bin")));
    }

    private static void RequireCandidate(string protocol)
    {
        bool official = Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_OFFICIAL_SIGNED") == "true";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_PACKAGE")) ||
            (!official && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_PUBLIC_KEY"))))
            Assert.Inconclusive("Requires the independent signed network v2 package gate.");
    }

    private static async Task VerifyHostAsync(string adapter, Dictionary<string, string> configuration, byte[] left, byte[] right)
    {
        string protocol = adapter.ToUpperInvariant();
        string package = Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_PACKAGE")!;
        bool official = Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_OFFICIAL_SIGNED") == "true";
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-network-v2", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var credentials = new RootCredentialStore();
        try
        {
            using RSA publisher = official ? MirrorPulseOfficialAdapterTrust.CreatePublicKey() : RSA.Create();
            if (!official) publisher.ImportFromPem(await File.ReadAllTextAsync(
                Environment.GetEnvironmentVariable("MP_" + protocol + "_V2_PUBLIC_KEY")!));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher,
                official ? MirrorPulseOfficialAdapterTrust.Signer : "MirrorPulse Dry Run");
            Assert.AreEqual("com.mirrorpulse.adapter." + adapter, installed.AdapterId.Value);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(2, installed.Manifest.Protocol.Minimum);
            Assert.AreEqual(2, installed.Manifest.Protocol.Maximum);
            configuration["root.offline.endpoint"] = "invalid-endpoint";
            configuration["root.offline.credentialReference"] = "must-not-be-requested";
            InstanceId instanceId = InstanceId.New();
            var instance = new AdapterInstance(installed.AdapterId, installed.InstallId, instanceId, protocol,
                configuration, ["left-credential", "right-credential"], Path.Combine(directory, "files"),
                Path.Combine(directory, "transfers"), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            RootRegistration Root(string key, bool enabled = true) => new(installed.AdapterId, instanceId, RootId.New(), key,
                key, key, false, enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled,
                DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var topology = new MirrorPulseAdapterTopology([installed], [instance], [Root("left"), Root("right"), Root("offline", false)]);
            await catalog.SaveAdapterTopologyAsync(topology);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentials);
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (true)
            {
                MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token);
                if (state?.Phase == "Connected") break;
                if (state?.Phase is "Worker failed" or "Worker error") Assert.Fail("Network Worker failed: " + state.LastErrorCode);
                await Task.Delay(25, timeout.Token);
            }
            CollectionAssert.AreEquivalent(EnabledCredentials, credentials.Requests);
            AdapterInstanceWorkerPayload launch = AdapterInstanceWorkerLaunchResolver.Resolve(topology, instanceId, WorkerSessionId.New(), runtime);
            SignedSmbV2WorkerProcessTests.VerifyPrivateRuntime(launch.LaunchRequest.ExecutablePath);
            var provider = new MirrorPulseDemandProvider(new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots),
                supervisor, new MirrorPulseAdapterDirectoryPageSource(supervisor));
            CloudProviderDirectoryPage top = await provider.FetchChildrenAsync(paths.SyncRootPath, ReadOnlyMemory<byte>.Empty, null, timeout.Token);
            foreach (string key in new[] { "left", "right" })
            {
                CloudPlaceholderSpec projected = top.Children.Single(item => item.Name == key);
                string folder = Path.Combine(paths.SyncRootPath, key);
                CloudProviderDirectoryPage page = await provider.FetchChildrenAsync(folder, projected.Identity.Encode(), null, timeout.Token);
                CloudFilePlaceholderSpec file = page.Children.OfType<CloudFilePlaceholderSpec>().Single(item => item.Name == "report.bin");
                byte[] expected = key == "left" ? left : right;
                Assert.AreEqual(expected.Length, file.Length);
                Assert.AreEqual(file.Identity.RemoteRevision, await supervisor.StatAsync(new(instanceId, "report.bin", key), timeout.Token));
                await using Stream read = await provider.OpenReadAsync(Path.Combine(folder, "report.bin"), file.Identity.Encode(),
                    file.Length, 0, file.Length, timeout.Token);
                var content = new byte[expected.Length];
                await read.ReadExactlyAsync(content, timeout.Token);
                CollectionAssert.AreEqual(expected, content);
            }
            using var replacement = new MemoryStream("must-not-overwrite"u8.ToArray());
            IOException refused = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await supervisor.UploadAsync(new(instanceId, "report.bin", null, replacement, replacement.Length,
                    Guid.NewGuid(), RootKey: "left"), timeout.Token));
            StringAssert.Contains(refused.Message, "ConditionalMutationUnavailable");
            Assert.IsEmpty(Directory.EnumerateFiles(instance.TransferCacheDirectory));
            Assert.IsNotNull(await supervisor.StatAsync(new(instanceId, "report.bin", "right"), timeout.Token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RootCredentialStore : ISecureCredentialStore
    {
        public List<string> Requests { get; } = [];
        public ValueTask SaveAsync(CredentialReference reference, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException("The fixture does not persist credentials."));
        public ValueTask<SecureCredentialValue?> TryGetAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            Requests.Add(reference.ReferenceId);
            string secret = reference.ReferenceId switch
            {
                "left-credential" => "secret-left",
                "right-credential" => "secret-right",
                _ => throw new InvalidDataException("Unexpected fixture credential request.")
            };
            return ValueTask.FromResult<SecureCredentialValue?>(new(reference, Encoding.UTF8.GetBytes(secret)));
        }
        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException("The fixture does not persist credentials."));
    }
}
