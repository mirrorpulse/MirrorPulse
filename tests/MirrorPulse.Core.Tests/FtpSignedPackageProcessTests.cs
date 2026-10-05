using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
[DoNotParallelize] // Both tests publish the same Worker project into its shared intermediate directory.
public sealed class FtpSignedPackageProcessTests
{
    [TestMethod]
    public async Task SignedPreviewAndLegacyVersionsCoexistAcrossSelectionRestartAndUninstall()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mirrorpulse-package-versions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string project = Path.Combine(FindRepositoryRoot(), "Adapters", "official",
                "MirrorPulse.Adapter.Ftp.Worker", "MirrorPulse.Adapter.Ftp.Worker.csproj");
            string x64 = Path.Combine(root, "publish-x64");
            string arm64 = Path.Combine(root, "publish-arm64");
            await PublishAsync(project, "win-x64", x64);
            await PublishAsync(project, "win-arm64", arm64);
            string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var storage = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            var paths = new CurrentUserAdapterPathProvider(Path.Combine(root, "installed"));
            var activation = new AdapterActivationPointerStore(paths);
            string[] versions = ["1.2.3.0", "1.2.3-preview.2", "1.2.3-preview.10", "1.2.3"];
            var installations = new List<InstalledAdapter>();
            InstanceId instanceId;
            using RSA publisher = RSA.Create(2048);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(storage))
            {
                foreach (string version in versions)
                {
                    string packagePath = Path.Combine(root, version + ".mpadapter");
                    SignedAdapterPackageBuildResult package = await OfficialProcessAdapterPackageBuilder.BuildAsync(
                        new OfficialProcessAdapterPackageInput("com.mirrorpulse.adapter.ftp", "FTP", version, x64, arm64),
                        packagePath, publisher, "Version Test Publisher");
                    InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(packagePath,
                        package.SignaturePath, paths.RootDirectory, runtime, publisher, "Version Test Publisher");
                    Assert.AreEqual(version, installed.Version);
                    string managedPath = paths.GetInstallationDirectory(installed.AdapterId, version, installed.InstallId);
                    Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
                    Directory.Move(installed.InstallationDirectory, managedPath);
                    installed = new InstalledAdapter(installed.Manifest, installed.InstallId, managedPath,
                        installed.PackageSha256, installed.Source, installed.SourceReference, installed.IsSigned,
                        installed.InstalledAt, installed.LifecycleState);
                    installations.Add(installed);
                }

                await catalog.SaveAdapterTopologyAsync(new(installations, [], []));
                AdapterInstance instance = await catalog.CreateInstanceAsync(installations[0].InstallId,
                    "Version test", new Dictionary<string, string>(), [], Path.Combine(root, "files"),
                    Path.Combine(root, "transfers"), enabled: false);
                instanceId = instance.InstanceId;
                await catalog.SelectInstanceInstallationAsync(instanceId, installations[2].InstallId);
                await activation.ActivateAsync(installations[2].AdapterId, installations[2].Version, installations[2].InstallId);
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(storage))
            {
                MirrorPulseAdapterTopology restored = await reopened.ReadAdapterTopologyAsync();
                CollectionAssert.AreEqual(versions, restored.Installations.Select(item => item.Version).ToArray());
                Assert.AreEqual(installations[2].InstallId, restored.Instances.Single().InstallId);
                Assert.AreEqual("1.2.3-preview.10", (await activation.ReadAsync(installations[2].AdapterId))?.Version);
                InstalledAdapter legacy = installations[0];
                AdapterUninstallResult removed = await new AdapterUninstallService(paths, activation).UninstallAsync(legacy);
                Assert.IsTrue(removed.InstallationRemoved);
                Assert.IsFalse(removed.ActivePointerCleared);
                await reopened.RemoveInstallationAsync(legacy.InstallId);
                Assert.IsTrue(Directory.Exists(installations[2].InstallationDirectory));
                await reopened.SelectInstanceInstallationAsync(instanceId, installations[3].InstallId);
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(storage))
            {
                MirrorPulseAdapterTopology restored = await reopened.ReadAdapterTopologyAsync();
                Assert.HasCount(3, restored.Installations);
                Assert.AreEqual(installations[3].InstallId, restored.Instances.Single().InstallId);
            }

            InstalledAdapter preview = installations[2];
            await VerifyInstalledWorkerStartsAsync(new SignedProcessAdapterInstallation(preview.InstallationDirectory,
                Path.Combine(preview.InstallationDirectory, preview.Manifest.Entrypoints[runtime].Replace('/', Path.DirectorySeparatorChar)),
                preview.AdapterId.ToString(), preview.Version));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishedArchitecturesInstallWithVerifiedSignatureAndNativeWorkerStarts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mirrorpulse-ftp-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string repository = FindRepositoryRoot();
            string project = Path.Combine(repository, "Adapters", "official",
                "MirrorPulse.Adapter.Ftp.Worker", "MirrorPulse.Adapter.Ftp.Worker.csproj");
            string x64 = Path.Combine(root, "publish-x64");
            string arm64 = Path.Combine(root, "publish-arm64");
            await PublishAsync(project, "win-x64", x64);
            await PublishAsync(project, "win-arm64", arm64);

            using RSA key = RSA.Create(2048);
            string packagePath = Path.Combine(root, "ftp.mpadapter");
            SignedAdapterPackageBuildResult package = await OfficialProcessAdapterPackageBuilder.BuildAsync(
                new OfficialProcessAdapterPackageInput("com.mirrorpulse.adapter.ftp", "FTP", "1.0.0", x64, arm64),
                packagePath, key, "MirrorPulse");
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                foreach (string runtime in new[] { "win-x64", "win-arm64" })
                {
                    Assert.IsNotNull(zip.GetEntry($"worker/{runtime}/MirrorPulse.Adapter.Worker.exe"));
                    Assert.IsNotNull(zip.GetEntry($"worker/{runtime}/MirrorPulse.Adapter.Ftp.Worker.dll"));
                    Assert.IsNotNull(zip.GetEntry($"worker/{runtime}/FluentFTP.dll"));
                }

                Assert.IsNotNull(zip.GetEntry(SignedProcessAdapterInstaller.EmbeddedSignaturePath));
            }

            string runtimeIdentifier = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64" : "win-x64";
            SignedProcessAdapterInstallation installation = await SignedProcessAdapterInstaller.InstallAsync(
                packagePath, package.SignaturePath, Path.Combine(root, "installed"),
                runtimeIdentifier, key, "MirrorPulse");
            Assert.AreEqual("com.mirrorpulse.adapter.ftp", installation.AdapterId);
            Assert.IsTrue(File.Exists(installation.ExecutablePath));
            Assert.IsFalse(File.Exists(Path.Combine(installation.InstallationDirectory,
                SignedProcessAdapterInstaller.EmbeddedSignaturePath.Replace('/', Path.DirectorySeparatorChar))));
            await VerifyInstalledWorkerStartsAsync(installation);

            SignedProcessAdapterInstallation standalone = await SignedProcessAdapterInstaller.InstallAsync(
                packagePath, Path.Combine(root, "missing.signature.json"), Path.Combine(root, "standalone"),
                runtimeIdentifier, key, "MirrorPulse");
            Assert.IsTrue(File.Exists(standalone.ExecutablePath));

            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"),
                Path.Combine(root, "product-data"));
            InstallId registeredId;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                InstalledAdapter registered = await catalog.InstallSignedAdapterAsync(packagePath,
                    package.SignaturePath, Path.Combine(root, "catalog-installed"), runtimeIdentifier,
                    key, "MirrorPulse");
                registeredId = registered.InstallId;
                Assert.AreEqual("com.mirrorpulse.adapter.ftp", registered.AdapterId.ToString());
                Assert.IsTrue(registered.IsSigned);
                Assert.IsTrue(File.Exists(Path.Combine(registered.InstallationDirectory, "manifest.json")));
            }

            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology topology = await reopened.ReadAdapterTopologyAsync();
                Assert.HasCount(1, topology.Installations);
                Assert.AreEqual(registeredId, topology.Installations[0].InstallId);
            }

            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry entry = archive.GetEntry("locales/en-US.json")!;
                entry.Delete();
                var replacement = archive.CreateEntry("locales/en-US.json");
                await using var stream = replacement.Open();
                await stream.WriteAsync("tampered"u8.ToArray());
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedProcessAdapterInstaller.InstallAsync(
                packagePath, package.SignaturePath, Path.Combine(root, "rejected"),
                runtimeIdentifier, key, "MirrorPulse"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyInstalledWorkerStartsAsync(SignedProcessAdapterInstallation installation)
    {
        var instance = InstanceId.New();
        var session = WorkerSessionId.New();
        string pipeName = $"mirrorpulse-ftp-package-{Guid.NewGuid():N}";
        await using var pipe = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
        using WorkerProcessHandle worker = WorkerProcessLauncher.Start(new WorkerLaunchRequest(
            instance, session, installation.ExecutablePath, installation.InstallationDirectory,
            ["--instance-id", instance.ToString(), "--worker-session-id", session.ToString(),
             "--pipe-name", pipeName]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            ControlFrameEnvelope hello = ControlFrameJsonCodec.Decode(
                await LengthPrefixedFrameReader.ReadAsync(pipe, timeout.Token));
            Assert.AreEqual("Hello", hello.MessageType);
            var invalidConfiguration = new ControlFrameEnvelope(1, "Ready", hello.RequestId,
                instance, session, true, JsonSerializer.SerializeToElement(new
                {
                    endpoint = "https://invalid.example/",
                    username = "user",
                    credentialReference = "reference",
                    securityMode = "Plain",
                }));
            byte[] payload = ControlFrameJsonCodec.Encode(invalidConfiguration);
            byte[] frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
            payload.CopyTo(frame.AsSpan(4));
            await pipe.WriteAsync(frame, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            ControlFrameEnvelope error = ControlFrameJsonCodec.Decode(
                await LengthPrefixedFrameReader.ReadAsync(pipe, timeout.Token));
            Assert.AreEqual("Error", error.MessageType);
            Assert.AreEqual("InvalidConfiguration", error.Payload.GetProperty("code").GetString());
            await worker.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
        }
    }

    private static async Task PublishAsync(string project, string runtime, string output)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[] { "publish", project, "--configuration", "Release", "--runtime",
            runtime, "--self-contained", "false", "--no-restore", "--output", output })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet publish did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        string outputText = await standardOutput;
        string errorText = await standardError;
        Assert.AreEqual(0, process.ExitCode, outputText + Environment.NewLine + errorText);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MirrorPulse.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("The MirrorPulse repository root was not found.");
    }
}
