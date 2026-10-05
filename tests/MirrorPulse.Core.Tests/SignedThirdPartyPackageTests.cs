using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedThirdPartyPackageTests
{
    [TestMethod]
    [DataRow("1.0.0", 1, true)]
    [DataRow("999.0.0", 1, false)]
    [DataRow("1.0.0", 2, true)]
    [DataRow("1.0.0", 3, false)]
    public async Task ExplicitlyTrustedPublisherCanInstallOnlyCompatibleStandalonePackage(
        string minimumProductVersion, int workerProtocol, bool compatible)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-third-party", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string packagePath = Path.Combine(root, "example.mpadapter");
        Directory.CreateDirectory(source);
        try
        {
            string adapterId = "com.example.cloud";
            string worker = "worker/win-x64/Example.Worker.exe";
            foreach (string runtime in new[] { "win-x64", "win-arm64" })
            {
                string path = Path.Combine(source, "worker", runtime, "Example.Worker.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] image = await File.ReadAllBytesAsync(Path.ChangeExtension(
                    typeof(MirrorPulse.Adapter.Ftp.Worker.FtpWorkerEntryMarker).Assembly.Location, ".exe"));
                using (var reader = new PEReader(new MemoryStream(image)))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(reader.PEHeaders.CoffHeaderStartOffset),
                        (ushort)(runtime == "win-x64" ? Machine.Amd64 : Machine.Arm64));
                }
                await File.WriteAllBytesAsync(path, image);
            }

            string localePath = Path.Combine(source, "locales", "en-US.json");
            Directory.CreateDirectory(Path.GetDirectoryName(localePath)!);
            await File.WriteAllTextAsync(localePath, "{\"displayName\":\"Example cloud\"}");
            await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                adapterId,
                publisher = "Example Publisher",
                version = "1.0.0",
                protocol = new { minimum = workerProtocol, maximum = workerProtocol },
                entrypoints = new Dictionary<string, string>
                {
                    ["win-x64"] = worker,
                    ["win-arm64"] = "worker/win-arm64/Example.Worker.exe",
                },
                installPolicy = new { maximumInstallations = (int?)null },
                instancePolicy = new
                {
                    maximumInstances = (int?)null,
                    maximumRootDefinitions = (int?)null
                },
                rootDefinitions = new[]
                {
                    new { key = "cloud", label = "Example cloud", directoryName = "Example cloud",
                        customEntry = false },
                },
                capabilities = new
                {
                    network = true,
                    sourceDirectory = false,
                    remoteChanges = true,
                    rangeRead = true
                },
                locales = new List<string> { "en-US" },
                localeMetadata = new Dictionary<string, object>
                {
                    ["en-US"] = new
                    {
                        displayName = "Example cloud",
                        resourcePath = "locales/en-US.json"
                    },
                },
                configurationFields = new List<object>
                {
                    new { key = "endpoint", label = "Server endpoint", kind = "Text",
                        required = true },
                    new { key = "credentialReference", label = "Password", kind = "Secret",
                        required = false },
                },
                minimumMirrorPulseVersion = minimumProductVersion,
            }));

            AdapterPackageBuildResult package = await AdapterPackageBuilder.BuildAsync(source, packagePath);
            using RSA publisherKey = RSA.Create(2048);
            byte[] signature = publisherKey.SignData(
                PackageManifestHasher.Canonicalize(package.FileManifest),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry metadata = archive.CreateEntry(
                    SignedProcessAdapterInstaller.EmbeddedSignaturePath);
                await using Stream output = metadata.Open();
                await JsonSerializer.SerializeAsync(output, new
                {
                    algorithm = RsaPackageSignatureVerifier.Algorithm,
                    signer = "Example Publisher",
                    signature = Convert.ToBase64String(signature),
                    files = package.FileManifest.Files.Select(file => new
                    {
                        path = file.Path,
                        length = file.Length,
                        sha256 = file.Sha256.ToString(),
                    }),
                });
            }

            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"),
                Path.Combine(root, "data"));
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            if (!compatible)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.InstallSignedAdapterAsync(packagePath,
                    Path.Combine(root, "missing.signature.json"), Path.Combine(root, "installed"),
                    "win-x64", publisherKey, "Example Publisher"));
                Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync()).Installations);
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "installed")));
                return;
            }
            InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(packagePath,
                Path.Combine(root, "missing.signature.json"), Path.Combine(root, "installed"),
                "win-x64", publisherKey, "Example Publisher");
            Assert.AreEqual(adapterId, installed.AdapterId.ToString());
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual("cloud", installed.Manifest.RootDefinitions.Single().Key);
            Assert.AreEqual("endpoint", installed.Manifest.ConfigurationFields[0].Key);
            Assert.AreEqual(AdapterConfigurationFieldKind.Secret,
                installed.Manifest.ConfigurationFields[1].Kind);
            Assert.IsTrue(File.Exists(Path.Combine(installed.InstallationDirectory,
                worker.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
