using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedLocalFormPackageTests
{
    [TestMethod]
    [TestCategory("LocalFormPackage")]
    public async Task SignedLocalFormSavesARequiredSourceAndStartsTheSingleRootWorker()
    {
        string? package = Environment.GetEnvironmentVariable("MP_LOCAL_FORM_PACKAGE");
        string? publicKey = Environment.GetEnvironmentVariable("MP_LOCAL_FORM_PUBLIC_KEY");
        bool official = Environment.GetEnvironmentVariable("MP_LOCAL_FORM_OFFICIAL_SIGNED") == "true";
        if (string.IsNullOrEmpty(package) || (!official && string.IsNullOrEmpty(publicKey)))
            Assert.Inconclusive("Requires the signed Local form package gate.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        CancellationToken token = deadline.Token;
        string directory = Directory.CreateTempSubdirectory("MirrorPulse-local-form-").FullName;
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        try
        {
            string source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "Local form source", token);
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            using RSA publisher = official ? MirrorPulseOfficialAdapterTrust.CreatePublicKey() : RSA.Create();
            if (!official) publisher.ImportFromPem(await File.ReadAllTextAsync(publicKey!, token));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(package!, package! + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher,
                official ? MirrorPulseOfficialAdapterTrust.Signer : "MirrorPulse Dry Run", token);
            Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
            Assert.AreEqual(2, installed.Manifest.SchemaVersion);
            Assert.AreEqual(1, installed.Manifest.InstancePolicy.MaximumRootDefinitions);
            AdapterPackagePresentation presentation = installed.Manifest.Presentation!;
            Assert.AreEqual("assets/logo.png", presentation.LogoPath);
            AdapterPackageFormControl field = presentation.Resolve("en-US").Controls.Single(control => control.Key is not null);
            Assert.AreEqual("sourceDirectory", field.Key);
            Assert.IsTrue(field.Required);

            var credentials = new WindowsCredentialManagerStore();
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            var request = new MirrorPulseCreateInstanceRequest(installed.InstallId.ToString(), "Documents",
                new Dictionary<string, string>(), new Dictionary<string, string> { ["local"] = "My files" }, null, Enabled: true);
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(() => provisioner.CreateAsync(request, token));
            AdapterInstance instance = await provisioner.CreateAsync(request with
            {
                Configuration = new Dictionary<string, string> { ["sourceDirectory"] = source },
            }, token);
            var topology = await catalog.ReadAdapterTopologyAsync(token);
            Assert.AreEqual("My files", topology.Roots.Single().Label);
            Assert.AreEqual(source, topology.Instances.Single().Configuration["sourceDirectory"]);

            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, credentials);
            await supervisor.StartAsync(topology);
            while (!supervisor.IsConnected(instance.InstanceId)) await Task.Delay(20, token);
            var page = await supervisor.ReadDirectoryPageAsync(new(instance.InstanceId, "", ReadOnlyMemory<byte>.Empty, 8, "local"), token);
            Assert.AreEqual("local.txt", page.Entries.Single().RelativePath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
