using System.Collections.ObjectModel;

namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Protocol versions supported by an Adapter Worker.
/// </summary>
public sealed record ProtocolVersionRange
{
    public ProtocolVersionRange(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public int Minimum { get; }

    public int Maximum { get; }
}

/// <summary>
/// Installation multiplicity declared by an Adapter package.
/// </summary>
public sealed record AdapterInstallPolicy
{
    public AdapterInstallPolicy(int? maximumInstallations)
    {
        MaximumInstallations = maximumInstallations;
    }

    public int? MaximumInstallations { get; }
}

/// <summary>
/// Instance and first-level root limits declared by an Adapter package.
/// </summary>
public sealed record AdapterInstancePolicy
{
    public AdapterInstancePolicy(int? maximumInstances, int? maximumRootDefinitions)
    {
        MaximumInstances = maximumInstances;
        MaximumRootDefinitions = maximumRootDefinitions;
    }

    public int? MaximumInstances { get; }

    public int? MaximumRootDefinitions { get; }
}

/// <summary>
/// Metadata read from an .mpadapter package.
/// </summary>
public sealed record AdapterManifest
{
    public AdapterManifest(
        int schemaVersion,
        AdapterId adapterId,
        string publisher,
        string version,
        ProtocolVersionRange protocol,
        IReadOnlyDictionary<string, string> entrypoints,
        AdapterInstallPolicy installPolicy,
        AdapterInstancePolicy instancePolicy,
        AdapterCapabilities capabilities,
        IReadOnlyList<string> locales,
        string minimumMirrorPulseVersion,
        IReadOnlyList<AdapterRootDefinition>? rootDefinitions = null,
        IReadOnlyList<AdapterLocaleMetadata>? localeMetadata = null,
        IReadOnlyList<AdapterConfigurationField>? configurationFields = null)
    {
        SchemaVersion = schemaVersion;
        AdapterId = adapterId;
        Publisher = publisher;
        Version = version;
        Protocol = protocol;
        Entrypoints = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(entrypoints, StringComparer.OrdinalIgnoreCase));
        InstallPolicy = installPolicy;
        InstancePolicy = instancePolicy;
        Capabilities = capabilities;
        Locales = locales.ToArray();
        MinimumMirrorPulseVersion = minimumMirrorPulseVersion;
        RootDefinitions = (rootDefinitions ?? []).ToArray();
        LocaleMetadata = new ReadOnlyDictionary<string, AdapterLocaleMetadata>(
            (localeMetadata ?? []).ToDictionary(item => item.Locale, StringComparer.OrdinalIgnoreCase));
        ConfigurationFields = (configurationFields ?? []).ToArray();
    }

    public int SchemaVersion { get; }

    public AdapterPackagePresentation? Presentation { get; init; }

    public AdapterId AdapterId { get; }

    public string Publisher { get; }

    public string Version { get; }

    public ProtocolVersionRange Protocol { get; }

    public IReadOnlyDictionary<string, string> Entrypoints { get; }

    public AdapterInstallPolicy InstallPolicy { get; }

    public AdapterInstancePolicy InstancePolicy { get; }

    public AdapterCapabilities Capabilities { get; }

    public IReadOnlyList<string> Locales { get; }

    public string MinimumMirrorPulseVersion { get; }

    /// <summary>
    /// First-level directory definitions declared by the Adapter.
    /// </summary>
    public IReadOnlyList<AdapterRootDefinition> RootDefinitions { get; }

    /// <summary>
    /// Optional display and resource metadata keyed by locale.
    /// </summary>
    public IReadOnlyDictionary<string, AdapterLocaleMetadata> LocaleMetadata { get; }

    /// <summary>Optional per-instance fields rendered and validated by MirrorPulse.</summary>
    public IReadOnlyList<AdapterConfigurationField> ConfigurationFields { get; }
}
