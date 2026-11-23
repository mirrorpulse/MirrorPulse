using System.Runtime.Versioning;
using System.Text;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using Windows.Storage.Provider;

namespace MirrorPulse.CfSharp.CrashProbe;

/// <summary>Reads and cleans up only the two exact registrations used by the disposable edition fixture.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal static class EditionShellRegistrationProbe
{
    internal static int Run(string edition, bool unregister)
    {
        var (prefix, provider) = edition switch
        {
            "standard" => ("MirrorPulse", Guid.Parse("d194eacd-c38c-49df-a238-49871f9c8d1b")),
            "custom" => ("MirrorPulse.sample-drive", Guid.Parse("7e2494ee-f27a-44cd-a564-15dc2eee476e")),
            _ => throw new ArgumentException("Unknown edition fixture.", nameof(edition)),
        };
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), prefix);
        byte[] identity = Encoding.UTF8.GetBytes($"{prefix}/cloud-files/v1");
        var definition = new MirrorPulseSyncRootDefinition(path, "0.1.0", provider, identity);
        MirrorPulseShellRegistrationProfile profile = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(definition);
        profile = profile with { RegistrationId = string.Concat(prefix, profile.RegistrationId.AsSpan("MirrorPulse".Length)) };
        if (!IsRegistered(profile.RegistrationId))
        {
            Console.WriteLine("Absent");
            return 0;
        }

        // Some Windows builds project an empty provider GUID. The product binds that
        // readback to the independently queried CFAPI registration on this exact path.
        MirrorPulseShellSyncRootRegistrar.RequireMatchingRegistration(profile);
        CloudSyncRootInfo cloud = CloudSyncRoot.Open(path).GetInfo();
        if (cloud.FileId == 0 || !string.Equals(cloud.Path, path, StringComparison.OrdinalIgnoreCase) ||
            cloud.ProviderName != MirrorPulseSyncRootRegistrationService.ProviderName || !cloud.SyncRootIdentity.SequenceEqual(identity))
        {
            throw new InvalidDataException("The registered CFAPI root does not match the edition fixture.");
        }

        if (unregister)
        {
            MirrorPulseSyncRootRegistrationCoordinator.CreateDefault().UnregisterForRemoval(definition, profile);
            if (IsRegistered(profile.RegistrationId)) throw new InvalidDataException("The edition Shell registration remained after cleanup.");
        }

        Console.WriteLine(unregister ? "Absent" : "Present");
        return 0;
    }

    private static bool IsRegistered(string id) => StorageProviderSyncRootManager.GetCurrentSyncRoots()
        .Any(root => string.Equals(root.Id, id, StringComparison.Ordinal));
}
