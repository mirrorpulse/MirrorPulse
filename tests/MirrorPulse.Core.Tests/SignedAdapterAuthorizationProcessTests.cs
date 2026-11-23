using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Compatibility;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedAdapterAuthorizationProcessTests
{
    private static readonly string[] AllowedWrites = ["account", "accessToken"];

    [TestMethod]
    [TestCategory("SignedAuthorizationPackage")]
    public async Task SignedXmlCallbackCreatesADeploymentThroughTheVersionedControlPipe()
    {
        string? package = Environment.GetEnvironmentVariable("MP_AUTHORIZATION_PACKAGE");
        string? publicKey = Environment.GetEnvironmentVariable("MP_AUTHORIZATION_PUBLIC_KEY");
        if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(publicKey))
            Assert.Inconclusive("Requires the signed SDK authorization sample.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        string root = Directory.CreateTempSubdirectory("MirrorPulse-signed-authorization-").FullName;
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        try
        {
            using RSA publisher = RSA.Create();
            publisher.ImportFromPem(await File.ReadAllTextAsync(publicKey, token));
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                Path.Combine(root, "installed"), runtime, publisher, "MirrorPulse Dry Run", token);
            AdapterPackageLocale form = installed.Manifest.Presentation!.Resolve("en-US");
            Assert.AreEqual("Sample storage Adapter", form.Title);
            AdapterPackageFormControl callback = form.Controls.Single(control => control.Kind == AdapterPackageFormKind.Callback);
            Assert.AreEqual("Authorize the test account", callback.Label);
            CollectionAssert.AreEqual(AllowedWrites, callback.WritableFields!.ToArray());

            var credentials = new MirrorPulseAdapterInstanceProvisionerTests.MemoryCredentialStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            await using var authorization = new AdapterAuthorizationCoordinator(catalog, credentials, provisioner, paths.DataRootPath);
            var dispatcher = new MirrorPulseControlDispatcher();
            MirrorPulseAdapterAuthorizationCommands.Register(dispatcher, authorization);
            new MirrorPulseLegacyCommandBridge(_ => Task.FromResult(new MirrorPulseAppStatusResponse(0, 0, [], [])),
                createInstance: async (request, requestToken) =>
                {
                    AdapterInstance created = await authorization.CreateAsync(request, request.AuthorizationIds, requestToken);
                    return new(0, 0, [], [], CreatedInstanceId: created.InstanceId.ToString());
                }).Register(dispatcher);
            string pipeName = "MirrorPulse-signed-authorization-" + Guid.NewGuid().ToString("N");
            var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipeName);
            var client = new MirrorPulseControlClient(new() { PipeName = pipeName, ExpectedHostProcessId = Environment.ProcessId });
            Task serving = server.ServeAsync(token);
            try
            {
                var values = new Dictionary<string, string>();
                var start = new InstanceAuthorizationStartArguments(installed.InstallId.ToString(), callback.Key!, values);
                AdapterAuthorizationStatus cancelled = await client.StartInstanceAuthorizationAsync(start, token);
                Assert.AreEqual(AdapterAuthorizationPhase.Cancelled,
                    (await client.CancelInstanceAuthorizationAsync(cancelled.AuthorizationId, token)).Phase);

                AdapterAuthorizationStatus pending = await client.StartInstanceAuthorizationAsync(start, token);
                Assert.AreEqual(AdapterAuthorizationPhase.Pending, pending.Phase);
                Assert.AreEqual(new Uri("https://example.test/mirrorpulse-test-authorization"), pending.InteractionUri);
                AdapterAuthorizationStatus ready = await client.PollInstanceAuthorizationAsync(pending.AuthorizationId, token);
                Assert.AreEqual(AdapterAuthorizationPhase.ReadyToSave, ready.Phase);
                Assert.AreEqual(0, credentials.Count);
                Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync(token)).Instances);

                var create = new InstanceCreateArguments(installed.InstallId.ToString(), "Signed sample", values,
                    new Dictionary<string, string> { ["sample"] = "Signed Sample" }, null, false, AuthorizationIds: [ready.AuthorizationId]);
                MirrorPulseAppStatusResponse result = await client.CreateInstanceAsync(create, token);
                Assert.AreEqual(ready.DeploymentId, result.CreatedInstanceId);
                MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync(token);
                AdapterInstance instance = topology.Instances.Single();
                Assert.AreEqual("Sample account", instance.Configuration["account"]);
                Assert.IsFalse(instance.Configuration.ContainsKey("accessToken"));
                Assert.AreEqual("sample-authorization-result", credentials.Read(instance.CredentialBindings!["accessToken"]));
                Assert.AreEqual(instance.InstanceId, topology.Roots.Single().InstanceId);
                await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(() => client.CreateInstanceAsync(create, token));
                Assert.HasCount(1, (await catalog.ReadAdapterTopologyAsync(token)).Instances);
            }
            finally { await timeout.CancelAsync(); await serving; }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
