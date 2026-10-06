using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

public sealed record OfficialProcessAdapterPackageInput(
    string AdapterId,
    string Label,
    string Version,
    string WinX64PublishDirectory,
    string WinArm64PublishDirectory,
    string WorkerExecutableName = "MirrorPulse.Adapter.Ftp.Worker.exe");

public sealed record SignedAdapterPackageBuildResult(
    AdapterPackageBuildResult Package,
    string SignaturePath);

/// <summary>
/// Packages both published Worker architectures and signs the detached file inventory.
/// </summary>
public static class OfficialProcessAdapterPackageBuilder
{
    private const string WorkerName = "MirrorPulse.Adapter.Worker.exe";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<SignedAdapterPackageBuildResult> BuildAsync(
        OfficialProcessAdapterPackageInput input,
        string outputPath,
        RSA signingKey,
        string signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!input.AdapterId.StartsWith("com.mirrorpulse.adapter.", StringComparison.Ordinal) ||
            !AdapterPackageVersion.TryParse(input.Version, out _) ||
            string.IsNullOrWhiteSpace(input.Label))
        {
            throw new ArgumentException("The official Adapter identity, label, or version is invalid.", nameof(input));
        }

        string staging = Path.Combine(Path.GetTempPath(), $"mirrorpulse-package-{Guid.NewGuid():N}");
        try
        {
            CopyPublishedWorker(input.WinX64PublishDirectory, Path.Combine(staging, "worker", "win-x64"),
                input.WorkerExecutableName);
            CopyPublishedWorker(input.WinArm64PublishDirectory, Path.Combine(staging, "worker", "win-arm64"),
                input.WorkerExecutableName);
            var manifest = new
            {
                schemaVersion = 1,
                adapterId = input.AdapterId,
                publisher = "MirrorPulse",
                version = input.Version,
                protocol = new { minimum = 1, maximum = 1 },
                entrypoints = new Dictionary<string, string>
                {
                    ["win-x64"] = $"worker/win-x64/{WorkerName}",
                    ["win-arm64"] = $"worker/win-arm64/{WorkerName}",
                },
                installPolicy = new { maximumInstallations = (int?)null },
                instancePolicy = new { maximumInstances = (int?)null, maximumRootDefinitions = (int?)null },
                rootDefinitions = new[]
                {
                    new { key = input.Label.ToLowerInvariant(), label = input.Label,
                        directoryName = input.Label, customEntry = false },
                },
                capabilities = new { network = true, sourceDirectory = false, remoteChanges = true, rangeRead = true },
                locales = new[] { "en-US" },
                localeMetadata = new Dictionary<string, object>
                {
                    ["en-US"] = new { displayName = "English (United States)", resourcePath = "locales/en-US.json" },
                },
                minimumMirrorPulseVersion = "1.0.0",
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
            string localeDirectory = Path.Combine(staging, "locales");
            Directory.CreateDirectory(localeDirectory);
            await File.WriteAllTextAsync(Path.Combine(localeDirectory, "en-US.json"),
                JsonSerializer.Serialize(new { displayName = input.Label }), cancellationToken).ConfigureAwait(false);

            AdapterPackageBuildResult package = await AdapterPackageBuilder.BuildAsync(staging, outputPath, cancellationToken)
                .ConfigureAwait(false);
            byte[] signature = signingKey.SignData(PackageManifestHasher.Canonicalize(package.FileManifest),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string signaturePath = package.PackagePath + ".signature.json";
            var envelope = new
            {
                algorithm = RsaPackageSignatureVerifier.Algorithm,
                signer,
                signature = Convert.ToBase64String(signature),
                files = package.FileManifest.Files.Select(file => new
                {
                    path = file.Path,
                    length = file.Length,
                    sha256 = file.Sha256.ToString(),
                }).ToArray(),
            };
            await File.WriteAllTextAsync(signaturePath, JsonSerializer.Serialize(envelope, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            using (var archive = ZipFile.Open(package.PackagePath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry embedded = archive.CreateEntry(
                    SignedProcessAdapterInstaller.EmbeddedSignaturePath, CompressionLevel.Optimal);
                await using Stream output = embedded.Open();
                await JsonSerializer.SerializeAsync(output, envelope, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new SignedAdapterPackageBuildResult(package, signaturePath);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void CopyPublishedWorker(string publishDirectory, string destination, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        if (Path.GetFileName(executableName) != executableName ||
            !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Worker executable name must be a plain .exe filename.",
                nameof(executableName));
        }

        string source = Path.GetFullPath(publishDirectory);
        if (!File.Exists(Path.Combine(source, executableName)))
        {
            throw new FileNotFoundException("The published Worker executable is missing.", source);
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Published Worker payloads cannot contain symbolic links.");
            }

            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.Move(Path.Combine(destination, executableName),
            Path.Combine(destination, WorkerName));
    }
}
