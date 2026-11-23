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
            AdapterInstance leftDeployment;
            AdapterInstance rightDeployment;
            string leftKey = "left";
            string rightKey = "right";
            ISecureCredentialStore credentialStore;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (installed.Manifest.Presentation is null)
            {
                var legacyInstanceId = InstanceId.New();
                leftDeployment = new(installed.AdapterId, installed.InstallId, legacyInstanceId, "WebDAV",
                    configuration, ["left-credential", "right-credential"], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
                    true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
                rightDeployment = leftDeployment;
                RootRegistration Root(string key, bool enabled = true) => new(installed.AdapterId, legacyInstanceId, RootId.New(), key,
                    key, key, false, enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled,
                    DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
                await catalog.SaveAdapterTopologyAsync(new([installed], [leftDeployment], [Root("left"), Root("right"), Root("offline", false)]), timeout.Token);
                credentialStore = new RootCredentialStore();
            }
            else
            {
                Assert.AreEqual("webdav", installed.Manifest.RootDefinitions.Single().Key);
                var signedCredentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
                var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, signedCredentials, paths.DataRootPath);
                leftDeployment = await provisioner.CreateAsync(new(installed.InstallId.ToString(), "left", new Dictionary<string, string>
                {
                    ["endpoint"] = left.Endpoint.AbsoluteUri,
                    ["authentication"] = "Basic",
                    ["username"] = "user-left",
                }, new Dictionary<string, string> { ["webdav"] = "left" }, null, true,
                    new Dictionary<string, string?> { ["password"] = "secret-left" }), timeout.Token);
                rightDeployment = await provisioner.CreateAsync(new(installed.InstallId.ToString(), "right", new Dictionary<string, string>
                {
                    ["endpoint"] = right.Endpoint.AbsoluteUri,
                    ["authentication"] = "Bearer",
                }, new Dictionary<string, string> { ["webdav"] = "right" }, null, true,
                    new Dictionary<string, string?> { ["password"] = "secret-right" }), timeout.Token);
                Assert.AreNotEqual(leftDeployment.CredentialBindings!["password"], rightDeployment.CredentialBindings!["password"]);
                leftKey = rightKey = "webdav";
                credentialStore = signedCredentials;
            }

            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync(timeout.Token);
            InstanceId instanceId = leftDeployment.InstanceId;
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentialStore);
            await supervisor.StartAsync(topology);
            while (!supervisor.IsConnected(instanceId) || !supervisor.IsConnected(rightDeployment.InstanceId))
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
                Guid.NewGuid(), RootKey: leftKey), timeout.Token);
            Assert.AreEqual(SignedWebDavV2HttpFixture.Revision(content), revision);
            Assert.IsNull(await supervisor.StatAsync(new(rightDeployment.InstanceId, "upload.bin", rightKey), timeout.Token));
            using var stale = new MemoryStream(content);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () =>
                await supervisor.UploadAsync(new(instanceId, "same.txt", "\"stale\"", stale, content.Length,
                    Guid.NewGuid(), RootKey: leftKey), timeout.Token));
            string oldRevision = (await supervisor.StatAsync(new(instanceId, "same.txt", leftKey), timeout.Token))!;
            using var replacement = new MemoryStream(content);
            await supervisor.UploadAsync(new(instanceId, "same.txt", oldRevision, replacement, content.Length,
                Guid.NewGuid(), RootKey: leftKey), timeout.Token);
            Assert.AreEqual("new content", Encoding.UTF8.GetString(left.Files["same.txt"]));
            Assert.AreEqual("right", Encoding.UTF8.GetString(right.Files["same.txt"]));
            Guid moveId = Guid.NewGuid();
            var move = new MirrorPulseWorkerMoveRequest(instanceId, "upload.bin", "moved.bin", revision, false, moveId, leftKey, leftKey);
            string movedRevision = await supervisor.MoveAsync(move, timeout.Token);
            Assert.AreEqual(movedRevision, await supervisor.MoveAsync(move, timeout.Token));
            CollectionAssert.AreEqual(content, left.Files["moved.bin"]);
            await supervisor.CreateDirectoryAsync(new(instanceId, leftKey, "empty", Guid.NewGuid()), timeout.Token);
            Assert.IsTrue(left.Directories.ContainsKey("empty"));
            await supervisor.DeleteAsync(new(instanceId, "empty", "\"directory\"", true, Guid.NewGuid(), leftKey), timeout.Token);
            Assert.IsFalse(left.Directories.ContainsKey("empty"));
            Assert.IsEmpty(left.Locks);
            await supervisor.DeleteAsync(new(instanceId, "moved.bin", movedRevision, false, Guid.NewGuid(), leftKey), timeout.Token);
            Assert.IsNull(await supervisor.StatAsync(new(instanceId, "moved.bin", leftKey), timeout.Token));
            Assert.IsEmpty(Directory.EnumerateFiles(leftDeployment.TransferCacheDirectory));
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
