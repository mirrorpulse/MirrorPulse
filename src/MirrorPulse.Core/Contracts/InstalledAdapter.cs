namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Source from which an installed .mpadapter package was acquired.
/// </summary>
public enum AdapterInstallSource
{
    LocalFile,
    OfficialRelease,
    Market,
    EditionBundle
}

/// <summary>
/// One installed copy of an Adapter package.
/// </summary>
public sealed record InstalledAdapter
{
    public InstalledAdapter(
        AdapterManifest manifest,
        InstallId installId,
        string installationDirectory,
        Sha256Digest packageSha256,
        AdapterInstallSource source,
        string? sourceReference,
        bool isSigned,
        DateTimeOffset installedAt,
        AdapterLifecycleState lifecycleState)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        Manifest = manifest;
        InstallId = installId;
        InstallationDirectory = installationDirectory;
        PackageSha256 = packageSha256;
        Source = source;
        SourceReference = sourceReference;
        IsSigned = isSigned;
        InstalledAt = installedAt;
        LifecycleState = lifecycleState;
    }

    public AdapterManifest Manifest { get; }

    public AdapterId AdapterId => Manifest.AdapterId;

    public string Version => Manifest.Version;

    public string Publisher => Manifest.Publisher;

    public InstallId InstallId { get; }

    public string InstallationDirectory { get; }

    public Sha256Digest PackageSha256 { get; }

    public AdapterInstallSource Source { get; }

    public string? SourceReference { get; }

    public bool IsSigned { get; }

    public DateTimeOffset InstalledAt { get; }

    public AdapterLifecycleState LifecycleState { get; }
}
