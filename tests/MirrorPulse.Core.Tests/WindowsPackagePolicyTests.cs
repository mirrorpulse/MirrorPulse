using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class WindowsPackagePolicyTests
{
    [TestMethod]
    [DataRow("worker/Adapter.exe:stream")]
    [DataRow("worker/CON.exe")]
    [DataRow("worker/com1.exe")]
    [DataRow("worker/LPT².dll")]
    [DataRow("worker/CONOUT$")]
    [DataRow("worker/adapter.exe.")]
    [DataRow("worker/adapter.exe ")]
    [DataRow("worker//adapter.exe")]
    [DataRow("worker/../adapter.exe")]
    [DataRow("worker/ADAPTE~1.exe")]
    [DataRow("worker/a\u0001.exe")]
    [DataRow("worker/e\u0301.dll")]
    public async Task ProductionInstallerRejectsWindowsAliasesBeforeExtraction(string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-path-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string package = Path.Combine(root, "invalid.mpadapter");
        string installed = Path.Combine(root, "installed");
        using RSA key = RSA.Create(2048);
        try
        {
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                byte[] bytes = "fixture"u8.ToArray();
                await using (Stream output = archive.CreateEntry(path).Open()) await output.WriteAsync(bytes);
                var inventory = new[] { new { path, length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
                byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(inventory);
                byte[] signature = key.SignData(canonical, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                await using Stream metadata = archive.CreateEntry(SignedProcessAdapterInstaller.EmbeddedSignaturePath).Open();
                await JsonSerializer.SerializeAsync(metadata, new
                {
                    algorithm = "RSA-SHA256",
                    signer = "Fixture",
                    signature = Convert.ToBase64String(signature),
                    files = inventory,
                });
            }
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => SignedProcessAdapterInstaller.InstallAsync(package,
                Path.Combine(root, "missing.json"), installed, "win-arm64", key, "Fixture"));
            Assert.IsFalse(Directory.Exists(installed));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CanonicalUnicodeAndOrdinarySpacesAreAllowedButParentFilesCannotAliasDirectories()
    {
        Assert.IsTrue(WindowsPackagePath.IsCanonical("locales/日本語.json"));
        Assert.IsTrue(WindowsPackagePath.IsCanonical("worker/Example Adapter.exe"));
        Assert.IsTrue(WindowsPackagePath.IsCanonical("worker/é.dll".Normalize(NormalizationForm.FormC)));
        var hash = new Sha256Digest(new string('A', 64));
        Assert.ThrowsExactly<ArgumentException>(() => new PackageFileManifest(
            [new("worker/a", 1, hash), new("WORKER/A/file.dll", 1, hash)]));
    }

    [TestMethod]
    public void CompatibilityUsesEffectiveVersionAndProtocolIntersection()
    {
        AdapterManifest Manifest(string minimum, ProtocolVersionRange protocol) => new(1,
            AdapterId.Parse("example.compatibility"), "Fixture", "1.0.0", protocol,
            new Dictionary<string, string> { ["win-x64"] = "worker/x64.exe", ["win-arm64"] = "worker/arm64.exe" },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null), new(true, false, true, true), ["en-US"], minimum);
        AdapterPackageCompatibility.Validate(Manifest("1.0", new(1, 2)), "win-arm64", new Version(1, 0, 0));
        AdapterPackageCompatibility.Validate(Manifest("1.0.0", new(2, 3)), "win-arm64", new Version(1, 0, 0));
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterPackageCompatibility.Validate(
            Manifest("2.0.0", new(1, 1)), "win-arm64", new Version(1, 0, 0)));
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterPayloadSelector.Select(Manifest("999.0.0", new(1, 1)), "win-arm64"));
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterPackageCompatibility.Validate(
            Manifest("1.0.0", new(3, 4)), "win-arm64", new Version(1, 0, 0)));
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterPackageCompatibility.Validate(
            Manifest("1.0.0", new(1, 1)), "linux-x64", new Version(1, 0, 0)));
    }

    [TestMethod]
    public void DeclaredRidMustMatchActualWindowsExecutable()
    {
        string executable = Path.ChangeExtension(typeof(MirrorPulse.Adapter.Ftp.Worker.FtpWorkerEntryMarker).Assembly.Location, ".exe");
        bool arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        AdapterPackageCompatibility.ValidateExecutable(executable, arm64 ? "win-arm64" : "win-x64");
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterPackageCompatibility.ValidateExecutable(executable, arm64 ? "win-x64" : "win-arm64"));
    }
}
