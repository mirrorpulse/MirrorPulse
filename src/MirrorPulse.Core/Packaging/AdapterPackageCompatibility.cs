using System.Reflection.PortableExecutable;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

/// <summary>Checks the selected process payload against the Host product version, protocol and architecture.</summary>
public static class AdapterPackageCompatibility
{
    public static Version CurrentProductVersion => typeof(ProductInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public const int CurrentWorkerProtocol = 1;

    public static void Validate(AdapterManifest manifest, string runtimeIdentifier, Version? productVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Diagnostic[] invalid = AdapterManifestValidator.Validate(manifest).Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (invalid.Length > 0 || !Version.TryParse(manifest.MinimumMirrorPulseVersion, out Version? minimum) ||
            Normalize(minimum) > Normalize(productVersion ?? CurrentProductVersion) ||
            manifest.Protocol.Minimum > CurrentWorkerProtocol || manifest.Protocol.Maximum < CurrentWorkerProtocol ||
            runtimeIdentifier is not ("win-x64" or "win-arm64") || !manifest.Entrypoints.ContainsKey(runtimeIdentifier))
        {
            throw new InvalidDataException("The Adapter requires an incompatible product version, Worker protocol or runtime.");
        }
    }

    public static void ValidateExecutable(string path, string runtimeIdentifier)
    {
        using var stream = File.OpenRead(path);
        try
        {
            using var reader = new PEReader(stream);
            Machine expected = runtimeIdentifier switch
            {
                "win-x64" => Machine.Amd64,
                "win-arm64" => Machine.Arm64,
                _ => throw new InvalidDataException("The Adapter runtime is unsupported."),
            };
            if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.CoffHeader.Machine != expected ||
                (reader.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
                (reader.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                throw new InvalidDataException("The Adapter Worker executable does not match its declared runtime.");
        }
        catch (BadImageFormatException)
        {
            throw new InvalidDataException("The Adapter Worker is not a valid Windows executable.");
        }
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
