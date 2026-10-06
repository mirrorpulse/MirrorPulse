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
public sealed class SignedWebDavV2WorkerProcessTests
{
    [TestMethod]
    [TestCategory("WebDavV2Package")]
    public async Task SignedWebDavV2KeepsCredentialsAndAcceptedContentBoundThroughProductionHost()
    {
        string? package = Environment.GetEnvironmentVariable("MP_WEBDAV_V2_PACKAGE");
        string? publicKey = Environment.GetEnvironmentVariable("MP_WEBDAV_V2_PUBLIC_KEY");
        bool official = Environment.GetEnvironmentVariable("MP_WEBDAV_V2_OFFICIAL_SIGNED") == "true";
        if (string.IsNullOrEmpty(package) || (!official && string.IsNullOrEmpty(publicKey)))
            Assert.Inconclusive("Requires the independent WebDAV v2 package gate.");
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-webdav-v2", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        await using var left = new SignedWebDavV2HttpFixture("left", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user-left:secret-left")));
        await using var right = new SignedWebDavV2HttpFixture("right", "Bearer secret-right");
        try
        {
            using RSA publisher = official ? MirrorPulseOfficialAdapterTrust.CreatePublicKey() : RSA.Create();
            if (!official) publisher.ImportFromPem(await File.ReadAllTextAsync(publicKey!));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher,
                official ? MirrorPulseOfficialAdapterTrust.Signer : "MirrorPulse Dry Run");
            Assert.AreEqual("com.mirrorpulse.adapter.webdav", installed.AdapterId.Value);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(2, installed.Manifest.Protocol.Minimum);
            Assert.AreEqual(2, installed.Manifest.Protocol.Maximum);
            var configuration = new Dictionary<string, string>
            {
                ["root.left.endpoint"] = left.Endpoint.AbsoluteUri,
                ["root.left.username"] = "user-left",
                ["root.left.credentialReference"] = "left-credential",
                ["root.right.endpoint"] = right.Endpoint.AbsoluteUri,
                ["root.right.authentication"] = "Bearer",
                ["root.right.credentialReference"] = "right-credential",
                ["root.offline.endpoint"] = "invalid-endpoint",
                ["root.offline.credentialReference"] = "unavailable-credential"
            };
            InstanceId instanceId = InstanceId.New();
            var instance = new AdapterInstance(installed.AdapterId, installed.InstallId, instanceId, "WebDAV",
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
            while ((await catalog.ReadInstanceRuntimeStateAsync(instanceId, timeout.Token))?.Phase != "Connected")
                await Task.Delay(25, timeout.Token);
            AdapterInstanceWorkerPayload launch = AdapterInstanceWorkerLaunchResolver.Resolve(topology, instanceId, WorkerSessionId.New(), runtime);
            VerifyPrivateRuntime(launch.LaunchRequest.ExecutablePath);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
            var provider = new MirrorPulseDemandProvider(router, supervisor, new MirrorPulseAdapterDirectoryPageSource(supervisor));
            var top = await provider.FetchChildrenAsync(paths.SyncRootPath, ReadOnlyMemory<byte>.Empty, null, timeout.Token);
            foreach ((string key, SignedWebDavV2HttpFixture server) in new[] { ("left", left), ("right", right) })
            {
                var projectedRoot = top.Children.Single(item => item.Name == key);
                string folder = Path.Combine(paths.SyncRootPath, key);
                var page = await provider.FetchChildrenAsync(folder, projectedRoot.Identity.Encode(), null, timeout.Token);
                var file = page.Children.OfType<CloudFilePlaceholderSpec>().Single(item => item.Name == "same.txt");
                Assert.AreEqual(SignedWebDavV2HttpFixture.Revision(server.Files["same.txt"]), file.Identity.RemoteRevision);
                await using Stream read = await provider.OpenReadAsync(Path.Combine(folder, "same.txt"), file.Identity.Encode(),
                    file.Length, 0, file.Length, timeout.Token);
                using var reader = new StreamReader(read);
                Assert.AreEqual(key, await reader.ReadToEndAsync(timeout.Token));
            }
            byte[] content = Encoding.UTF8.GetBytes("new content");
            using var upload = new MemoryStream(content);
            string revision = await supervisor.UploadAsync(new(instanceId, "upload.bin", null, upload, content.Length,
                Guid.NewGuid(), RootKey: "left"), timeout.Token);
            Assert.AreEqual(SignedWebDavV2HttpFixture.Revision(content), revision);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "upload.bin", "right"), timeout.Token));
            using var stale = new MemoryStream(content);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () =>
                await supervisor.UploadAsync(new(instanceId, "same.txt", "\"stale\"", stale, content.Length,
                    Guid.NewGuid(), RootKey: "left"), timeout.Token));
            string oldRevision = (await supervisor.StatAsync(new(instanceId, "same.txt", "left"), timeout.Token))!;
            using var replacement = new MemoryStream(content);
            await supervisor.UploadAsync(new(instanceId, "same.txt", oldRevision, replacement, content.Length,
                Guid.NewGuid(), RootKey: "left"), timeout.Token);
            Assert.AreEqual("new content", Encoding.UTF8.GetString(left.Files["same.txt"]));
            Assert.AreEqual("right", Encoding.UTF8.GetString(right.Files["same.txt"]));
            Guid moveId = Guid.NewGuid();
            var move = new MirrorPulseWorkerMoveRequest(instanceId, "upload.bin", "moved.bin", revision, false, moveId, "left", "left");
            string movedRevision = await supervisor.MoveAsync(move, timeout.Token);
            Assert.AreEqual(movedRevision, await supervisor.MoveAsync(move, timeout.Token));
            CollectionAssert.AreEqual(content, left.Files["moved.bin"]);
            await supervisor.CreateDirectoryAsync(new(instanceId, "left", "empty", Guid.NewGuid()), timeout.Token);
            Assert.IsTrue(left.Directories.ContainsKey("empty"));
            await supervisor.DeleteAsync(new(instanceId, "empty", "\"directory\"", true, Guid.NewGuid(), "left"), timeout.Token);
            Assert.IsFalse(left.Directories.ContainsKey("empty"));
            Assert.IsEmpty(left.Locks);
            await supervisor.DeleteAsync(new(instanceId, "moved.bin", movedRevision, false, Guid.NewGuid(), "left"), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "moved.bin", "left"), timeout.Token));
            Assert.IsEmpty(Directory.EnumerateFiles(instance.TransferCacheDirectory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void VerifyPrivateRuntime(string executable)
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
            ValueTask.FromResult<SecureCredentialValue?>(reference.ReferenceId switch
            {
                "left-credential" => new(reference, Encoding.UTF8.GetBytes("secret-left")),
                "right-credential" => new(reference, Encoding.UTF8.GetBytes("secret-right")),
                _ => throw new InvalidDataException("Unexpected fixture credential request.")
            });

        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException("The fixture does not persist credentials."));
    }
}
