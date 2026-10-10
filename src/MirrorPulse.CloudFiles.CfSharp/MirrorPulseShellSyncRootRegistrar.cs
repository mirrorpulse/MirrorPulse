using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using MirrorPulse.Core.CloudFiles;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseShellRegistrationProfile(
    string RegistrationId,
    string SyncRootPath,
    Guid ProviderId,
    string DisplayName,
    string ProviderVersion,
    string IconResource);

/// <summary>
/// Registers the single MP root with Explorer's Shell integration. CfSharp owns the separate
/// Cloud Files registration and callback lifecycle.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class MirrorPulseShellSyncRootRegistrar
{
    public const string DefaultIconResource = "%SystemRoot%\\System32\\imageres.dll,-102";
    // WinRT has no named None member; zero disables metadata tracking.
    public const StorageProviderInSyncPolicy ContentInSyncPolicy = StorageProviderInSyncPolicy.Default;

    public static MirrorPulseShellRegistrationProfile CreateProfile(
        MirrorPulseSyncRootDefinition definition,
        string currentUserSid,
        string displayName = MirrorPulseSyncRootRegistrationService.ProviderName,
        string iconResource = DefaultIconResource)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconResource);

        return new(
            CreateRegistrationId(currentUserSid),
            definition.Path,
            definition.ProviderId,
            displayName.Trim(),
            definition.ProviderVersion,
            iconResource.Trim());
    }

    public static MirrorPulseShellRegistrationProfile CreateCurrentUserProfile(
        MirrorPulseSyncRootDefinition definition,
        string displayName = MirrorPulseSyncRootRegistrationService.ProviderName,
        string iconResource = DefaultIconResource)
    {
        string currentUserSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return CreateProfile(definition, currentUserSid, displayName, iconResource);
    }

    public static string CreateRegistrationId(string currentUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        return $"MirrorPulse!{currentUserSid.Trim()}!Default";
    }

    public static async ValueTask<bool> RegisterAsync(
        MirrorPulseShellRegistrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();

        string path = Path.GetFullPath(profile.SyncRootPath);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("The MirrorPulse sync root must exist before Shell registration.");
        }

        bool alreadyRegistered = TryGetRegisteredPath(profile.RegistrationId, out string? existingPath);
        if (alreadyRegistered && !string.Equals(Path.GetFullPath(existingPath!), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The MirrorPulse Shell registration belongs to another sync root.");
        }
        if (alreadyRegistered && StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId).ProviderId != profile.ProviderId)
            throw new InvalidOperationException("The existing Shell registration belongs to another provider.");

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
        cancellationToken.ThrowIfCancellationRequested();
        StorageProviderSyncRootInfo registration = new()
        {
            Id = profile.RegistrationId,
            Path = folder,
            ProviderId = profile.ProviderId,
            DisplayNameResource = profile.DisplayName,
            IconResource = profile.IconResource,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = ContentInSyncPolicy,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            Version = profile.ProviderVersion,
            AllowPinning = true,
            ShowSiblingsAsGroup = false,
            Context = CryptographicBuffer.ConvertStringToBinary(path, BinaryStringEncoding.Utf8),
        };
        try
        {
            StorageProviderSyncRootManager.Register(registration);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"Shell sync-root registration failed with HRESULT 0x{exception.HResult:X8}.",
                exception);
        }
        try
        {
            StorageProviderSyncRootInfo actual = StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId);
            if (actual.InSyncPolicy != ContentInSyncPolicy ||
                actual.PopulationPolicy != StorageProviderPopulationPolicy.Full ||
                !string.Equals(actual.Path.Path, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The actual Shell root policy does not match content synchronization.");
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070490) && !HasPackageIdentity())
        {
            // Unpackaged development Hosts can register successfully while Shell cannot
            // resolve the registration ID. The coordinator still requires CFAPI's actual
            // None policy; packaged Hosts must also pass the Shell readback.
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"Shell sync-root readback failed with HRESULT 0x{exception.HResult:X8}.",
                exception);
        }
        return alreadyRegistered;
    }

    private static bool HasPackageIdentity()
    {
        try { _ = Windows.ApplicationModel.Package.Current.Id.Name; return true; }
        catch (InvalidOperationException) { return false; }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80073D54)) { return false; }
    }

    public static bool TryGetRegisteredPath(string registrationId, out string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
        EnsureSupported();
        try
        {
            path = StorageProviderSyncRootManager
                .GetSyncRootInformationForId(registrationId)
                .Path.Path;
            return true;
        }
        catch (COMException)
        {
            path = null;
            return false;
        }
    }

    public static void Unregister(MirrorPulseShellRegistrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!TryGetRegisteredPath(profile.RegistrationId, out string? existingPath))
        {
            return;
        }

        if (!string.Equals(
            Path.GetFullPath(existingPath!),
            Path.GetFullPath(profile.SyncRootPath),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The MirrorPulse Shell registration belongs to another sync root.");
        }

        StorageProviderSyncRootManager.Unregister(profile.RegistrationId);
    }

    private static void EnsureSupported()
    {
        if (!StorageProviderSyncRootManager.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Shell sync-root registration is unavailable.");
        }
    }
}
