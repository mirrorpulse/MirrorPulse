using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

/// <summary>
/// Resolves deterministic per-user installation paths without using machine-wide locations.
/// </summary>
public sealed class CurrentUserAdapterPathProvider
{
    public CurrentUserAdapterPathProvider(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorPulse",
            "adapters",
            "installed"));
    }

    public string RootDirectory { get; }

    public string GetAdapterDirectory(AdapterId adapterId) =>
        Path.Combine(RootDirectory, adapterId.ToString());

    public string GetVersionDirectory(AdapterId adapterId, string version)
    {
        ValidateVersion(version);
        return Path.Combine(GetAdapterDirectory(adapterId), version);
    }

    public string GetInstallationDirectory(AdapterId adapterId, string version, InstallId installId) =>
        Path.Combine(GetVersionDirectory(adapterId, version), installId.ToString());

    private static void ValidateVersion(string? version)
    {
        if (!AdapterPackageVersion.TryParse(version, out _))
        {
            throw new ArgumentException("Adapter versions must be canonical path-safe values.", nameof(version));
        }
    }
}
