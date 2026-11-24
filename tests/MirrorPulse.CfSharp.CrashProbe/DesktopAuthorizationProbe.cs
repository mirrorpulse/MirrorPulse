using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

internal static class DesktopAuthorizationProbe
{
    private const string AdapterId = "example.desktop-authorization";
    private const string Signer = "Desktop authorization fixture";
    private const string DeploymentName = "Authorization desktop fixture";

    public static async Task SeedAsync(MirrorPulseStoragePaths paths, string scratch, string logo)
    {
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
        if (topology.Installations.Count != 0 || topology.Instances.Count != 0 || topology.Roots.Count != 0)
            throw new InvalidOperationException("The desktop authorization fixture requires an empty catalog.");

        string payload = Path.Combine(scratch, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "forms"));
        Directory.CreateDirectory(Path.Combine(payload, "locales"));
        File.Copy(logo, Path.Combine(payload, "logo.png"));
        await File.WriteAllTextAsync(Path.Combine(payload, "forms", "config.xml"), """
            <form version="1">
              <text key="account" labelKey="account" required="true" />
              <password key="token" labelKey="token" required="true" />
              <callback id="desktop-authorize" labelKey="authorize"><write key="token" /></callback>
            </form>
            """);
        await File.WriteAllTextAsync(Path.Combine(payload, "locales", "en-US.json"), """
            {"title":"Desktop authorization","description":"Synthetic authorization fixture","account":"Account",
             "token":"Access token","authorize":"Authorize the test account"}
            """);
        await File.WriteAllTextAsync(Path.Combine(payload, "manifest.json"), $$$"""
            {"schemaVersion":2,"adapterId":"{{{AdapterId}}}","publisher":"{{{Signer}}}","version":"1.0.0",
             "protocol":{"minimum":2,"maximum":2},
             "entrypoints":{"win-arm64":"worker/win-arm64/MirrorPulse.Worker.ProtocolFixture.exe",
                            "win-x64":"worker/win-x64/MirrorPulse.Worker.ProtocolFixture.exe"},
             "installPolicy":{"maximumInstallations":null},"instancePolicy":{"maximumInstances":null,"maximumRootDefinitions":1},
             "rootDefinitions":[{"key":"fixture","label":"Authorization files","directoryName":"Authorization files","customEntry":false}],
             "capabilities":{"network":false,"sourceDirectory":false,"remoteChanges":false,"rangeRead":true},
             "locales":["en-US"],"localeMetadata":{"en-US":{"displayName":"English","resourcePath":"locales/en-US.json"}},
             "minimumMirrorPulseVersion":"1.0.0",
             "presentation":{"titleKey":"title","descriptionKey":"description","logoPath":"logo.png","formPath":"forms/config.xml"}}
            """);
        string packagePath = Path.Combine(scratch, "authorization.mpadapter");
        AdapterPackageBuildResult package = await AdapterPackageBuilder.BuildAsync(payload, packagePath);
        using RSA key = RSA.Create(3072);
        byte[] signature = key.SignData(PackageManifestHasher.Canonicalize(package.FileManifest),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            await using Stream metadata = archive.CreateEntry(SignedProcessAdapterInstaller.EmbeddedSignaturePath).Open();
            await JsonSerializer.SerializeAsync(metadata, new
            {
                algorithm = RsaPackageSignatureVerifier.Algorithm,
                signer = Signer,
                signature = Convert.ToBase64String(signature),
                files = package.FileManifest.Files.Select(file => new { path = file.Path, length = file.Length, sha256 = file.Sha256.ToString() }),
            });
        }

        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        InstalledAdapter installed = await catalog.InstallSignedAdapterAsync(packagePath, packagePath + ".signature.json",
            Path.Combine(paths.DataRootPath, "adapters", "installed"), runtime, key, Signer);
        Console.WriteLine(JsonSerializer.Serialize(new { installId = installed.InstallId.ToString(), sha256 = installed.PackageSha256.ToString() }));
    }

    public static async Task FinishAsync(MirrorPulseStoragePaths paths, InstallId expected, string previousReference)
    {
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
        InstalledAdapter installed = topology.Installations.Single();
        if (installed.InstallId != expected || installed.AdapterId.Value != AdapterId || !installed.IsSigned)
            throw new InvalidDataException("The desktop fixture does not match the signed installation.");
        if (previousReference == "cleanup" && topology.Instances.Count == 0) return;

        AdapterInstance instance = topology.Instances.Single();
        if (instance.InstallId != expected || instance.DisplayName != DeploymentName ||
            instance.CredentialBindings is not { Count: 1 } references || !references.TryGetValue("token", out string? current))
            throw new InvalidDataException("The saved deployment does not match the desktop fixture.");

        var store = new WindowsCredentialManagerStore();
        CredentialReference currentReference = Reference(current);
        try
        {
            if (previousReference == "cleanup") return;
            using SecureCredentialValue? old = await store.TryGetAsync(Reference(previousReference));
            using SecureCredentialValue? replacement = await store.TryGetAsync(currentReference);
            if (old is not null || replacement is null || !replacement.Value.Span.SequenceEqual("fixture-authorization"u8))
                throw new InvalidDataException("The saved credential did not replace the previous authorization.");
            Console.WriteLine("Credential replacement verified.");
        }
        finally
        {
            await store.DeleteAsync(currentReference);
        }
    }

    private static CredentialReference Reference(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("The authorization reference is invalid.");
        return new(id, CredentialKind.Password, AdapterId, CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
    }
}
