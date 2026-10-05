using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CfSharp;
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
public sealed class SignedSmbV2WorkerProcessTests
{
    [TestMethod]
    [TestCategory("SmbV2Package")]
    public async Task SignedSmbV2KeepsWindowsCredentialsAndAcceptedContentBoundThroughProductionHost()
    {
        string? package = Environment.GetEnvironmentVariable("MP_SMB_V2_PACKAGE");
        string? publicKey = Environment.GetEnvironmentVariable("MP_SMB_V2_PUBLIC_KEY");
        bool official = Environment.GetEnvironmentVariable("MP_SMB_V2_OFFICIAL_SIGNED") == "true";
        if (string.IsNullOrEmpty(package) || (!official && string.IsNullOrEmpty(publicKey)) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MP_SMB_FIXTURE_BACKING")))
            Assert.Inconclusive("Requires the independent SMB v2 package gate with disposable shares.");
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-smb-v2", Guid.NewGuid().ToString("N"));
        string sourceName = "host-" + Guid.NewGuid().ToString("N");
        string backing = Path.GetFullPath(Fixture("BACKING"));
        string source = Path.GetFullPath(Path.Combine(backing, sourceName));
        Assert.IsTrue(source.StartsWith(Path.TrimEndingDirectorySeparator(backing) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        try
        {
            using RSA publisher = official ? MirrorPulseOfficialAdapterTrust.CreatePublicKey() : RSA.Create();
            if (!official) publisher.ImportFromPem(await File.ReadAllTextAsync(publicKey!));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher,
                official ? MirrorPulseOfficialAdapterTrust.Signer : "MirrorPulse Dry Run");
            Assert.AreEqual("com.mirrorpulse.adapter.smb", installed.AdapterId.Value);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(2, installed.Manifest.Protocol.Minimum);
            Assert.AreEqual(2, installed.Manifest.Protocol.Maximum);
            var configuration = new Dictionary<string, string>();
            foreach (string key in new[] { "left", "right" })
            {
                string rootSource = Path.Combine(source, key);
                Directory.CreateDirectory(rootSource);
                await File.WriteAllTextAsync(Path.Combine(rootSource, "same.txt"), key);
                string side = key.ToUpperInvariant();
                configuration["root." + key + ".networkPath"] = Path.Combine(Fixture(side + "_SHARE"), sourceName, key);
                configuration["root." + key + ".username"] = Fixture(side + "_USER");
                configuration["root." + key + ".domain"] = Environment.MachineName;
                configuration["root." + key + ".credentialReference"] = key + "-credential";
            }
            configuration["root.offline.networkPath"] = "invalid-network-path";
            configuration["root.offline.credentialReference"] = "must-not-be-requested";
            InstanceId instanceId = InstanceId.New();
            var instance = new AdapterInstance(installed.AdapterId, installed.InstallId, instanceId, "SMB",
                configuration, ["left-credential", "right-credential"], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
                true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            RootRegistration Root(string key, bool enabled = true) => new(installed.AdapterId, instanceId, RootId.New(), key,
                key, key, false, enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled,
                DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var topology = new MirrorPulseAdapterTopology([installed], [instance], [Root("left"), Root("right"), Root("offline", false)]);
            await catalog.SaveAdapterTopologyAsync(topology);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new RootCredentialStore());
            await supervisor.StartAsync(topology);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (true)
            {
                MirrorPulseInstanceRuntimeState? state = await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token);
                if (state?.Phase == "Connected") break;
                if (state?.Phase is "Worker failed" or "Worker error") Assert.Fail("SMB Worker failed: " + state.LastErrorCode);
                await Task.Delay(25, timeout.Token);
            }
            AdapterInstanceWorkerPayload launch = AdapterInstanceWorkerLaunchResolver.Resolve(topology, instanceId, WorkerSessionId.New(), runtime);
            VerifyPrivateRuntime(launch.LaunchRequest.ExecutablePath);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor, new MirrorPulseAdapterDirectoryPageSource(supervisor));
            CloudProviderDirectoryPage top = await provider.FetchChildrenAsync(paths.SyncRootPath, ReadOnlyMemory<byte>.Empty, null, timeout.Token);
            foreach (string key in new[] { "left", "right" })
            {
                CloudPlaceholderSpec projectedRoot = top.Children.Single(item => item.Name == key);
                string folder = Path.Combine(paths.SyncRootPath, key);
                CloudProviderDirectoryPage page = await provider.FetchChildrenAsync(folder, projectedRoot.Identity.Encode(), null, timeout.Token);
                CloudFilePlaceholderSpec file = page.Children.OfType<CloudFilePlaceholderSpec>().Single(item => item.Name == "same.txt");
                await using Stream read = await provider.OpenReadAsync(Path.Combine(folder, "same.txt"), file.Identity.Encode(),
                    file.Length, 0, file.Length, timeout.Token);
                using var reader = new StreamReader(read);
                Assert.AreEqual(key, await reader.ReadToEndAsync(timeout.Token));
            }
            byte[] content = "new SMB content"u8.ToArray();
            using var upload = new MemoryStream(content);
            string revision = await supervisor.UploadAsync(new(instanceId, "upload.bin", null, upload, content.Length,
                Guid.NewGuid(), RootKey: "left"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "upload.bin", "right"), timeout.Token));
            string oldRevision = (await supervisor.StatAsync(new(instanceId, "same.txt", "left"), timeout.Token))!;
            using var replacement = new MemoryStream(content);
            await supervisor.UploadAsync(new(instanceId, "same.txt", oldRevision, replacement, content.Length,
                Guid.NewGuid(), RootKey: "left"), timeout.Token);
            using var stale = new MemoryStream(content);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () =>
                await supervisor.UploadAsync(new(instanceId, "same.txt", oldRevision, stale, content.Length,
                    Guid.NewGuid(), RootKey: "left"), timeout.Token));
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(Path.Combine(source, "left", "same.txt"), timeout.Token));
            Assert.AreEqual("right", await File.ReadAllTextAsync(Path.Combine(source, "right", "same.txt"), timeout.Token));
            var move = new MirrorPulseWorkerMoveRequest(instanceId, "upload.bin", "moved.bin", revision, false, Guid.NewGuid(), "left", "left");
            string movedRevision = await supervisor.MoveAsync(move, timeout.Token);
            Assert.AreEqual(movedRevision, await supervisor.MoveAsync(move, timeout.Token));
            await supervisor.CreateDirectoryAsync(new(instanceId, "left", "empty", Guid.NewGuid()), timeout.Token);
            string directoryRevision = (await supervisor.StatAsync(new(instanceId, "empty", "left"), timeout.Token))!;
            await supervisor.DeleteAsync(new(instanceId, "empty", directoryRevision, true, Guid.NewGuid(), "left"), timeout.Token);
            Assert.IsFalse(Directory.Exists(Path.Combine(source, "left", "empty")));
            await supervisor.DeleteAsync(new(instanceId, "moved.bin", movedRevision, false, Guid.NewGuid(), "left"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "moved.bin", "left"), timeout.Token));
            Assert.IsEmpty(Directory.EnumerateFiles(instance.TransferCacheDirectory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (Directory.Exists(source)) Directory.Delete(source, true);
        }
    }

    private static string Fixture(string name) => Environment.GetEnvironmentVariable("MP_SMB_FIXTURE_" + name) ??
        throw new InvalidOperationException("The disposable SMB fixture is incomplete.");

    internal static void VerifyPrivateRuntime(string executable)
    {
        string expected = Path.Combine(Path.GetDirectoryName(executable)!, "coreclr.dll");
        bool found = false;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (!process.ProcessName.Contains("MirrorPulse", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                    ProcessModule module = process.Modules.Cast<ProcessModule>().Single(item => item.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
                    Assert.AreEqual(expected, module.FileName, ignoreCase: true);
                    found = true;
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        Assert.IsTrue(found, "The installed Worker must load the CLR from its own payload.");
    }

    private sealed class RootCredentialStore : ISecureCredentialStore
    {
        public ValueTask SaveAsync(CredentialReference reference, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException("The fixture does not persist credentials."));
        public ValueTask<SecureCredentialValue?> TryGetAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecureCredentialValue?>(new(reference, Encoding.UTF8.GetBytes(reference.ReferenceId switch
            {
                "left-credential" => Fixture("LEFT_PASSWORD"),
                "right-credential" => Fixture("RIGHT_PASSWORD"),
                _ => throw new InvalidDataException("Unexpected fixture credential request.")
            })));
        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException("The fixture does not persist credentials."));
    }
}
