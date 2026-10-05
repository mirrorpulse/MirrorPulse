using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

public sealed record SignedProcessAdapterInstallation(
    string InstallationDirectory,
    string ExecutablePath,
    string AdapterId,
    string Version);

/// <summary>
/// Verifies every package byte before publishing an executable payload into the user install directory.
/// </summary>
public static class SignedProcessAdapterInstaller
{
    private const long MaximumUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const string EmbeddedSignaturePath = "META-INF/mirrorpulse/signature.json";

    public static async Task<SignedProcessAdapterInstallation> InstallAsync(
        string packagePath,
        string signaturePath,
        string installationRoot,
        string runtimeIdentifier,
        RSA trustedKey,
        string trustedSigner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentNullException.ThrowIfNull(trustedKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedSigner);
        if (runtimeIdentifier is not ("win-x64" or "win-arm64"))
        {
            throw new PlatformNotSupportedException("The Adapter runtime is unsupported.");
        }

        await using var packageStream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry[] embeddedSignatures = archive.Entries
            .Where(entry => entry.FullName == EmbeddedSignaturePath).ToArray();
        if (embeddedSignatures.Length > 1 || embeddedSignatures.Any(entry => entry.Length > 1024 * 1024))
        {
            throw new InvalidDataException("The embedded Adapter signature is duplicated or too large.");
        }

        ZipArchiveEntry? embeddedSignature = embeddedSignatures.SingleOrDefault();
        using JsonDocument metadata = embeddedSignature is not null
            ? await JsonDocument.ParseAsync(embeddedSignature.Open(), cancellationToken: cancellationToken)
                .ConfigureAwait(false)
            : JsonDocument.Parse(await File.ReadAllTextAsync(signaturePath, cancellationToken)
                .ConfigureAwait(false));
        JsonElement root = metadata.RootElement;
        string algorithm = root.GetProperty("algorithm").GetString() ?? string.Empty;
        string signer = root.GetProperty("signer").GetString() ?? string.Empty;
        byte[] signature = Convert.FromBase64String(root.GetProperty("signature").GetString() ?? string.Empty);
        var declaredFiles = root.GetProperty("files").EnumerateArray().Select(file =>
            new PackageFileEntry(
                file.GetProperty("path").GetString() ?? string.Empty,
                file.GetProperty("length").GetInt64(),
                Sha256Digest.Parse(file.GetProperty("sha256").GetString() ?? string.Empty))).ToArray();
        var declaredManifest = new PackageFileManifest(declaredFiles);

        if (archive.Entries.Count == 0 || archive.Entries.Count > 4096 ||
            archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            archive.Entries.Any(entry => !WindowsPackagePath.IsCanonical(entry.FullName) || string.IsNullOrEmpty(entry.Name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
        {
            throw new InvalidDataException("The Adapter package contains unsupported entries.");
        }

        var actualFiles = new List<PackageFileEntry>(archive.Entries.Count);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName == EmbeddedSignaturePath)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > MaximumUncompressedBytes - totalBytes)
            {
                throw new InvalidDataException("The Adapter package exceeds the uncompressed size limit.");
            }

            totalBytes += entry.Length;
            await using Stream stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long readBytes = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                readBytes += count;
            }

            if (readBytes != entry.Length)
            {
                throw new InvalidDataException("An Adapter package entry length is inconsistent.");
            }

            actualFiles.Add(new PackageFileEntry(entry.FullName, readBytes,
                new Sha256Digest(Convert.ToHexString(hash.GetHashAndReset()))));
        }

        var actualManifest = new PackageFileManifest(actualFiles);
        if (!PackageManifestHasher.Canonicalize(actualManifest).AsSpan()
            .SequenceEqual(PackageManifestHasher.Canonicalize(declaredManifest)))
        {
            throw new InvalidDataException("The Adapter package content does not match its signed inventory.");
        }

        var verifier = new RsaPackageSignatureVerifier(trustedKey, trustedSigner);
        Result<PackageSignatureVerification> verification = await verifier.VerifyAsync(packageStream,
            declaredManifest, new AdapterPackageSignature(algorithm, signer, signature), cancellationToken)
            .ConfigureAwait(false);
        if (verification.IsFailure || !verification.Value.IsValid)
        {
            throw new CryptographicException("The Adapter package signature is invalid or untrusted.");
        }

        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The Adapter manifest is missing.");
        if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("The Adapter manifest is too large.");
        await using Stream manifestStream = manifestEntry.Open();
        AdapterManifest manifest = await AdapterPackageManifestReader.ReadAsync(manifestStream, cancellationToken).ConfigureAwait(false);
        AdapterPackageCompatibility.Validate(manifest, runtimeIdentifier);
        string adapterId = manifest.AdapterId.ToString();
        string version = manifest.Version;
        string entrypoint = manifest.Entrypoints[runtimeIdentifier];
        if (!AdapterId.TryParse(adapterId, out _) ||
            !Version.TryParse(version, out _) ||
            !actualFiles.Any(file => string.Equals(file.Path, entrypoint, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The Adapter manifest identity or Worker entrypoint is invalid.");
        }

        string rootDirectory = Path.GetFullPath(installationRoot);
        Directory.CreateDirectory(rootDirectory);
        string stagedDirectory = Path.Combine(rootDirectory, $".staging-{Guid.NewGuid():N}");
        string installedDirectory = Path.Combine(rootDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagedDirectory);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName == EmbeddedSignaturePath)
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(stagedDirectory,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The Adapter package path escapes the installation directory.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using Stream input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            foreach (var payload in manifest.Entrypoints)
            {
                AdapterPackageCompatibility.ValidateExecutable(Path.Combine(stagedDirectory,
                    payload.Value.Replace('/', Path.DirectorySeparatorChar)), payload.Key);
            }
            Directory.Move(stagedDirectory, installedDirectory);
            return new SignedProcessAdapterInstallation(installedDirectory,
                Path.Combine(installedDirectory, entrypoint.Replace('/', Path.DirectorySeparatorChar)),
                adapterId, version);
        }
        finally
        {
            if (Directory.Exists(stagedDirectory))
            {
                Directory.Delete(stagedDirectory, recursive: true);
            }
        }
    }
}
