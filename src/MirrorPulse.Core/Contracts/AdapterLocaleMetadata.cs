using System.Collections.ObjectModel;

namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Display and resource metadata for one Adapter locale.
/// </summary>
public sealed record AdapterLocaleMetadata
{
    public AdapterLocaleMetadata(string locale, string displayName, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!IsSafeResourcePath(resourcePath))
        {
            throw new ArgumentException("Locale resource paths must be safe relative JSON paths.", nameof(resourcePath));
        }

        Locale = locale;
        DisplayName = displayName;
        ResourcePath = resourcePath;
    }

    public string Locale { get; }

    public string DisplayName { get; }

    public string ResourcePath { get; }

    private static bool IsSafeResourcePath(string? path) =>
        WindowsPackagePath.IsCanonical(path) && path!.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves a requested locale and falls back to en-US when needed.
/// </summary>
public sealed class AdapterLocaleCatalog
{
    private readonly ReadOnlyDictionary<string, AdapterLocaleMetadata> _metadata;

    public AdapterLocaleCatalog(IEnumerable<AdapterLocaleMetadata> metadata, string fallbackLocale = "en-US")
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackLocale);
        _metadata = new ReadOnlyDictionary<string, AdapterLocaleMetadata>(
            metadata.ToDictionary(item => item.Locale, StringComparer.OrdinalIgnoreCase));
        FallbackLocale = fallbackLocale;
        if (!_metadata.ContainsKey(FallbackLocale))
        {
            throw new ArgumentException("The locale catalog must contain its fallback locale.", nameof(metadata));
        }
    }

    public string FallbackLocale { get; }

    public IReadOnlyDictionary<string, AdapterLocaleMetadata> Metadata => _metadata;

    public AdapterLocaleMetadata Resolve(string? requestedLocale)
    {
        if (!string.IsNullOrWhiteSpace(requestedLocale) && _metadata.TryGetValue(requestedLocale, out var requested))
        {
            return requested;
        }

        return _metadata[FallbackLocale];
    }
}
