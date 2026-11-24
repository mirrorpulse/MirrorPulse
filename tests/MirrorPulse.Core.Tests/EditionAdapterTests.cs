using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class EditionAdapterTests
{
    private const string BundledId = "com.example.storage";
    private const string Signer = "Edition fixture";
    private static readonly string[] BundledLocales = ["en-US"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BundledImportIsDurableAndDirectEditionProtectsItsOnlyDeployment(bool direct)
    {
        DirectoryInfo fixture = Directory.CreateTempSubdirectory("MirrorPulse-edition-");
        try
        {
            string host = Path.Combine(fixture.FullName, "host");
            string bundle = Path.Combine(host, "edition-adapters", $"adapter.{BundledId}.mpadapter");
            string installed = Path.Combine(fixture.FullName, "installed");
            var paths = new MirrorPulseStoragePaths(Path.Combine(fixture.FullName, "sync"), Path.Combine(fixture.FullName, "data"));
            var edition = new MirrorPulseEdition("example", "Example Drive", Guid.NewGuid(), BundledId, direct);
            using RSA signer = RSA.Create(2048);
            InstallId importId;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, edition))
            {
                Task ImportAsync() => catalog.InstallEditionAdaptersAsync(host, installed, "win-x64", signer, Signer);

                await WritePackageAsync(bundle, "com.example.other", signer);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(ImportAsync);
                Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync()).Installations);
                Assert.IsEmpty(Directory.EnumerateFileSystemEntries(installed));

                await WritePackageAsync(bundle, BundledId, signer);
                await ImportAsync();
                await ImportAsync();
                InstalledAdapter import = (await catalog.ReadAdapterTopologyAsync()).Installations.Single();
                importId = import.InstallId;
                Assert.AreEqual(AdapterInstallSource.EditionBundle, import.Source);
                Assert.AreEqual(edition.Id, import.SourceReference);
                Assert.IsTrue(import.IsSigned);
                Assert.HasCount(1, Directory.EnumerateFileSystemEntries(installed));

                if (direct)
                {
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RemoveInstallationAsync(importId));
                    Task<AdapterInstance> CreateAsync() => catalog.CreateInstanceAsync(importId, "Files",
                        new Dictionary<string, string>(), [], Path.Combine(fixture.FullName, "cache"),
                        Path.Combine(fixture.FullName, "transfer"));
                    await CreateAsync();
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(CreateAsync);
                }
                else
                {
                    await catalog.RemoveInstallationAsync(importId);
                }
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths, edition);
            await reopened.InstallEditionAdaptersAsync(host, installed, "win-x64", signer, Signer);
            var topology = await reopened.ReadAdapterTopologyAsync();
            if (direct)
            {
                Assert.AreEqual(importId, topology.Installations.Single().InstallId);
                Assert.AreEqual(importId, topology.Instances.Single().InstallId);
                Assert.AreEqual("files", topology.Roots.Single().UniquenessKey);
            }
            else
            {
                Assert.IsEmpty(topology.Installations);
            }
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static async Task WritePackageAsync(string packagePath, string adapterId, RSA signer)
    {
        string source = Path.Combine(Path.GetDirectoryName(packagePath)!, "source");
        Directory.CreateDirectory(source);
        var entrypoints = new Dictionary<string, string>
        {
            ["win-x64"] = "worker/x64.exe",
            ["win-arm64"] = "worker/arm64.exe",
        };
        Directory.CreateDirectory(Path.Combine(source, "worker"));
        byte[] image = await File.ReadAllBytesAsync(Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe"));
        using (var reader = new PEReader(new MemoryStream(image)))
        {
            foreach (var payload in entrypoints)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(reader.PEHeaders.CoffHeaderStartOffset),
                    (ushort)(payload.Key == "win-x64" ? Machine.Amd64 : Machine.Arm64));
                await File.WriteAllBytesAsync(Path.Combine(source, payload.Value), image);
            }
        }

        Directory.CreateDirectory(Path.Combine(source, "locales"));
        await File.WriteAllTextAsync(Path.Combine(source, "locales", "en-US.json"), """{"title":"Files","description":"Edition fixture"}""");
        await File.WriteAllTextAsync(Path.Combine(source, "form.xml"), """<form version="1"><heading textKey="title" /></form>""");
        await File.WriteAllBytesAsync(Path.Combine(source, "logo.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aB9sAAAAASUVORK5CYII="));
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            adapterId,
            publisher = Signer,
            version = "1.0.0",
            protocol = new { minimum = 2, maximum = 2 },
            entrypoints,
            installPolicy = new { maximumInstallations = (int?)null },
            instancePolicy = new { maximumInstances = (int?)null, maximumRootDefinitions = 1 },
            rootDefinitions = new[] { new { key = "files", label = "Files", directoryName = "Files", customEntry = false } },
            capabilities = new { network = false, sourceDirectory = false, remoteChanges = false, rangeRead = true },
            locales = BundledLocales,
            localeMetadata = new Dictionary<string, object> { ["en-US"] = new { displayName = "English", resourcePath = "locales/en-US.json" } },
            minimumMirrorPulseVersion = "1.0.0",
            presentation = new { titleKey = "title", descriptionKey = "description", logoPath = "logo.png", formPath = "form.xml" },
        }));
        File.Delete(packagePath);
        AdapterPackageBuildResult built = await AdapterPackageBuilder.BuildAsync(source, packagePath);
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        await using var metadata = archive.CreateEntry(SignedProcessAdapterInstaller.EmbeddedSignaturePath).Open();
        byte[] signature = signer.SignData(PackageManifestHasher.Canonicalize(built.FileManifest),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await JsonSerializer.SerializeAsync(metadata, new
        {
            algorithm = RsaPackageSignatureVerifier.Algorithm,
            signer = Signer,
            signature = Convert.ToBase64String(signature),
            files = built.FileManifest.Files.Select(file => new { path = file.Path, length = file.Length, sha256 = file.Sha256.Hexadecimal }),
        });
    }
}
