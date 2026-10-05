using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

public sealed record AdapterReleaseReference(
    AdapterId AdapterId,
    string Version,
    string AssetName,
    Uri PackageUri,
    Uri ReleasePageUri,
    DateTimeOffset? PublishedAt);

/// <summary>
/// Converts a parsed latest release into the immutable version/URL reference MP stores.
/// </summary>
public static class AdapterReleaseReferenceResolver
{
    public static AdapterReleaseReference Resolve(AdapterId adapterId, LatestAdapterRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var packageAssets = release.Assets
            .Where(asset => asset.Name.EndsWith(".mpadapter", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (packageAssets.Length != 1)
        {
            throw new InvalidDataException("A latest Adapter release must contain exactly one .mpadapter asset.");
        }

        var version = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
        if (!AdapterPackageVersion.TryParse(version, out _))
        {
            throw new InvalidDataException("The Adapter release tag must contain a canonical package version.");
        }

        var asset = packageAssets[0];
        return new AdapterReleaseReference(
            adapterId,
            version,
            asset.Name,
            asset.DownloadUri,
            release.ReleasePageUri,
            release.PublishedAt);
    }
}
