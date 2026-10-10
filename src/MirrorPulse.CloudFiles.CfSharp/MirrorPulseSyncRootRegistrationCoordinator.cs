using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;

namespace MirrorPulse.CloudFiles.CfSharp;

public interface IMirrorPulseShellRootRegistry
{
    ValueTask<bool> RegisterAsync(MirrorPulseShellRegistrationProfile profile, CancellationToken cancellationToken);

    void Unregister(MirrorPulseShellRegistrationProfile profile);
}

public interface IMirrorPulseCloudRootRegistry
{
    void EnsureCompatible(MirrorPulseSyncRootDefinition definition);

    void Register(MirrorPulseSyncRootDefinition definition);

    void Unregister(string syncRootPath);
}

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsMirrorPulseShellRootRegistry : IMirrorPulseShellRootRegistry
{
    public ValueTask<bool> RegisterAsync(
        MirrorPulseShellRegistrationProfile profile,
        CancellationToken cancellationToken) =>
        MirrorPulseShellSyncRootRegistrar.RegisterAsync(profile, cancellationToken);

    public void Unregister(MirrorPulseShellRegistrationProfile profile) =>
        MirrorPulseShellSyncRootRegistrar.Unregister(profile);
}

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CfSharpMirrorPulseCloudRootRegistry : IMirrorPulseCloudRootRegistry
{
    private const int RootNotRegisteredHResult = unchecked((int)0x80070186);
    private const int RootHasNoCloudFilesMetadataHResult = unchecked((int)0x80070001);

    public void EnsureCompatible(MirrorPulseSyncRootDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CloudSyncRootInfo info;
        try
        {
            info = CloudSyncRoot.Open(definition.Path).GetInfo();
        }
        catch (CloudFilesException exception) when (exception.HResult is
            RootNotRegisteredHResult or RootHasNoCloudFilesMetadataHResult)
        {
            return;
        }

        if (!string.Equals(info.ProviderName, MirrorPulseSyncRootRegistrationService.ProviderName, StringComparison.Ordinal)
            || !info.SyncRootIdentity.SequenceEqual(definition.Identity))
        {
            throw new InvalidDataException("The existing Cloud Files registration belongs to another root identity.");
        }
    }

    public void Register(MirrorPulseSyncRootDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        new MirrorPulseSyncRootRegistrationService(new CfSharpSyncRootRegistrar()).Register(definition);
        CloudSyncRootInfo registered = CloudSyncRoot.Open(definition.Path).GetInfo();
        if (!string.Equals(registered.Path, definition.Path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registered.ProviderName, MirrorPulseSyncRootRegistrationService.ProviderName, StringComparison.Ordinal) ||
            !string.Equals(registered.ProviderVersion, definition.ProviderVersion, StringComparison.Ordinal) ||
            registered.InSyncPolicy != CloudInSyncPolicy.None ||
            registered.PopulationPolicy != CloudPopulationPolicy.Full ||
            registered.HardLinkPolicy != CloudHardLinkPolicy.Disallowed ||
            !registered.SyncRootIdentity.SequenceEqual(definition.Identity))
        {
            throw new InvalidDataException("CfSharp registration metadata does not match the MirrorPulse root.");
        }
    }

    public void Unregister(string syncRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        try
        {
            CloudSyncRoot.Open(syncRootPath).Unregister();
        }
        catch (CloudFilesException exception) when (exception.HResult == RootNotRegisteredHResult)
        {
            // A previous removal may have completed before its Shell step failed.
        }
    }
}

/// <summary>
/// Pairs Explorer and CfSharp's persistent registrations without making ordinary Host shutdown
/// an account-removal operation.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseSyncRootRegistrationCoordinator
{
    private readonly IMirrorPulseShellRootRegistry _shell;
    private readonly IMirrorPulseCloudRootRegistry _cloud;

    public MirrorPulseSyncRootRegistrationCoordinator(
        IMirrorPulseShellRootRegistry shell,
        IMirrorPulseCloudRootRegistry cloud)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(cloud);
        _shell = shell;
        _cloud = cloud;
    }

    public static MirrorPulseSyncRootRegistrationCoordinator CreateDefault() =>
        new(new WindowsMirrorPulseShellRootRegistry(), new CfSharpMirrorPulseCloudRootRegistry());

    public async ValueTask EnsureRegisteredAsync(
        MirrorPulseSyncRootDefinition definition,
        MirrorPulseShellRegistrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidatePair(definition, profile);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(definition.Path);
        _cloud.EnsureCompatible(definition);

        bool shellAlreadyExisted = await _shell.RegisterAsync(profile, cancellationToken).ConfigureAwait(false);
        try
        {
            _cloud.Register(definition);
        }
        catch
        {
            if (!shellAlreadyExisted)
            {
                try
                {
                    _shell.Unregister(profile);
                }
                catch
                {
                    // Preserve the Cloud Files error. A later explicit repair can remove the
                    // matching Shell registration without touching another user's root.
                }
            }

            throw;
        }
    }

    public void UnregisterForRemoval(
        MirrorPulseSyncRootDefinition definition,
        MirrorPulseShellRegistrationProfile profile)
    {
        ValidatePair(definition, profile);
        _cloud.EnsureCompatible(definition);
        _cloud.Unregister(definition.Path);
        _shell.Unregister(profile);
    }

    private static void ValidatePair(
        MirrorPulseSyncRootDefinition definition,
        MirrorPulseShellRegistrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(definition.Path, Path.GetFullPath(profile.SyncRootPath), StringComparison.OrdinalIgnoreCase)
            || definition.ProviderId != profile.ProviderId)
        {
            throw new ArgumentException("The Shell and Cloud Files registrations must identify the same MP root.", nameof(profile));
        }
    }
}
