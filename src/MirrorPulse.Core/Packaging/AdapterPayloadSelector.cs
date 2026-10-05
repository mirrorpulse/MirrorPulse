using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

public sealed record AdapterPayloadSelection(string RuntimeIdentifier, string Entrypoint);

/// <summary>
/// Selects the manifest entrypoint for one supported Windows architecture.
/// </summary>
public static class AdapterPayloadSelector
{
    private static readonly string[] SupportedRuntimeIdentifiers = ["win-x64", "win-arm64"];

    public static AdapterPayloadSelection Select(AdapterManifest manifest, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (!SupportedRuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException($"The Adapter runtime '{runtimeIdentifier}' is not supported.");
        }
        AdapterPackageCompatibility.Validate(manifest, runtimeIdentifier);

        if (!manifest.Entrypoints.TryGetValue(runtimeIdentifier, out var entrypoint) || string.IsNullOrWhiteSpace(entrypoint))
        {
            throw new InvalidOperationException($"The Adapter manifest has no entrypoint for '{runtimeIdentifier}'.");
        }

        return new AdapterPayloadSelection(runtimeIdentifier, entrypoint);
    }
}
