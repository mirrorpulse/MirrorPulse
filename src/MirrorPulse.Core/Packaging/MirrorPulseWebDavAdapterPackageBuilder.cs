using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

public sealed record MirrorPulseWebDavAdapterPackageInput(
    string Version,
    string WinX64WorkerPath,
    string WinArm64WorkerPath);

/// <summary>
/// Builds the official WebDAV Adapter package from its two Worker payloads.
/// </summary>
public static class MirrorPulseWebDavAdapterPackageBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static async Task<AdapterPackageBuildResult> BuildAsync(
        MirrorPulseWebDavAdapterPackageInput input,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidateVersion(input.Version);
        ValidateWorker(input.WinX64WorkerPath, nameof(input.WinX64WorkerPath));
        ValidateWorker(input.WinArm64WorkerPath, nameof(input.WinArm64WorkerPath));

        var staging = Path.Combine(Path.GetTempPath(), $"mirrorpulse-webdav-adapter-{Guid.NewGuid():N}");
        try
        {
            var x64Directory = Path.Combine(staging, "worker", "win-x64");
            var arm64Directory = Path.Combine(staging, "worker", "win-arm64");
            Directory.CreateDirectory(x64Directory);
            Directory.CreateDirectory(arm64Directory);
            var x64Name = Path.GetFileName(input.WinX64WorkerPath);
            var arm64Name = Path.GetFileName(input.WinArm64WorkerPath);
            File.Copy(input.WinX64WorkerPath, Path.Combine(x64Directory, x64Name));
            File.Copy(input.WinArm64WorkerPath, Path.Combine(arm64Directory, arm64Name));

            var manifest = new
            {
                schemaVersion = 1,
                adapterId = "com.mirrorpulse.adapter.webdav",
                publisher = "MirrorPulse",
                version = input.Version,
                protocol = new { minimum = 1, maximum = 1 },
                entrypoints = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["win-x64"] = $"worker/win-x64/{x64Name}",
                    ["win-arm64"] = $"worker/win-arm64/{arm64Name}",
                },
                installPolicy = new { maximumInstallations = (int?)null },
                instancePolicy = new { maximumInstances = (int?)null, maximumRootDefinitions = (int?)null },
                rootDefinitions = new[]
                {
                    new { key = "webdav", label = "WebDAV", directoryName = "WebDAV", customEntry = false },
                },
                capabilities = new { network = true, sourceDirectory = false, remoteChanges = true, rangeRead = true },
                locales = new[] { "en-US" },
                localeMetadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["en-US"] = new { displayName = "English (United States)", resourcePath = "locales/en-US.json" },
                },
                minimumMirrorPulseVersion = "1.0.0",
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, SerializerOptions), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(staging, "locales"));
            await File.WriteAllTextAsync(Path.Combine(staging, "locales", "en-US.json"), "{\"displayName\":\"WebDAV\"}", cancellationToken).ConfigureAwait(false);
            return await AdapterPackageBuilder.BuildAsync(staging, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void ValidateWorker(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The WebDAV Worker payload was not found.", path);
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("WebDAV Worker payloads must be executable files.", parameterName);
        }
    }

    private static void ValidateVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!AdapterPackageVersion.TryParse(version, out _))
        {
            throw new ArgumentException("The Adapter version must be canonical.", nameof(version));
        }
    }
}
