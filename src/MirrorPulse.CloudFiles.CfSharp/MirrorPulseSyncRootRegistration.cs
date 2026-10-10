using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Application-owned inputs for the single MirrorPulse Cloud Files sync root.
/// </summary>
public sealed record MirrorPulseSyncRootDefinition
{
    public MirrorPulseSyncRootDefinition(string path, string providerVersion, Guid providerId, ReadOnlySpan<byte> identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);
        if (providerId == Guid.Empty)
        {
            throw new ArgumentException("The provider ID must not be empty.", nameof(providerId));
        }

        Path = System.IO.Path.GetFullPath(path.Trim());
        ProviderVersion = providerVersion.Trim();
        ProviderId = providerId;
        Identity = identity.ToArray();
    }

    public string Path { get; }

    public string ProviderVersion { get; }

    public Guid ProviderId { get; }

    public byte[] Identity { get; }
}

public sealed record MirrorPulseSyncRootRegistrationResult(
    string Path,
    Guid ProviderId,
    string ProviderName,
    string ProviderVersion);

public interface IMirrorPulseSyncRootRegistrar
{
    MirrorPulseSyncRootRegistrationResult Register(string path, SyncRootRegistrationOptions options);
}

/// <summary>
/// Thin production boundary around the CfSharp persistent registration call.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class CfSharpSyncRootRegistrar : IMirrorPulseSyncRootRegistrar
{
    public MirrorPulseSyncRootRegistrationResult Register(string path, SyncRootRegistrationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        var root = CloudSyncRoot.Register(path, options);
        return new(root.Path, options.ProviderId, options.ProviderName, options.ProviderVersion);
    }
}

/// <summary>
/// Builds the fixed MirrorPulse provider identity and registers its one managed sync root.
/// </summary>
public sealed class MirrorPulseSyncRootRegistrationService
{
    public const string ProviderName = "MirrorPulse";

    private readonly IMirrorPulseSyncRootRegistrar _registrar;

    public MirrorPulseSyncRootRegistrationService(IMirrorPulseSyncRootRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        _registrar = registrar;
    }

    public MirrorPulseSyncRootRegistrationResult Register(MirrorPulseSyncRootDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Directory.CreateDirectory(definition.Path);

        var options = SyncRootRegistrationOptions.CreateBuilder(ProviderName, definition.ProviderVersion)
            .WithProviderId(definition.ProviderId)
            .WithSyncRootIdentity(definition.Identity)
            .WithHydrationPolicy(CloudHydrationPolicy.Full, CloudHydrationPolicyModifiers.None)
            .WithPopulationPolicy(CloudPopulationPolicy.Full)
            .WithInSyncPolicy(CloudInSyncPolicy.None)
            .AllowHardLinks(false)
            .WithRootMarkedInSync(true)
            .WithExistingRegistrationUpdate(true)
            .Build();

        return _registrar.Register(definition.Path, options);
    }
}
