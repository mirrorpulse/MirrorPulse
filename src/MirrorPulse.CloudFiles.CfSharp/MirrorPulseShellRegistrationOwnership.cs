namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Fresh public CFAPI registration facts, read without starting a provider session.</summary>
internal sealed record MirrorPulseShellCloudFilesRoot(string Path, long FileId, string ProviderName, string Identity);

internal static class MirrorPulseShellRegistrationOwnership
{
    internal static byte[] CreateContextIdentity(MirrorPulseShellRegistrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrEmpty(profile.CloudFilesRootIdentity))
            throw new InvalidOperationException("Shell registration requires the configured Cloud Files root identity bytes.");
        return Convert.FromBase64String(profile.CloudFilesRootIdentity);
    }
    internal static void Require(MirrorPulseShellRegistrationProfile expected, string registeredId, string registeredPath,
        Guid registeredProvider, Func<MirrorPulseShellCloudFilesRoot> readCloudFilesRoot)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(readCloudFilesRoot);
        if (expected.ProviderId == Guid.Empty || !string.Equals(registeredId, expected.RegistrationId, StringComparison.Ordinal) ||
            !SamePath(registeredPath, expected.SyncRootPath))
            throw new InvalidOperationException("The existing Shell registration does not match the expected root ID and path.");
        if (registeredProvider == expected.ProviderId) return;
        if (registeredProvider != Guid.Empty)
            throw new InvalidOperationException($"The existing Shell registration belongs to another provider: expected={expected.ProviderId:D}; actual={registeredProvider:D}.");
        // An empty WinRT readback is absence of evidence. Require the same fresh
        // public Cloud Files identity used by the Host's registration coordinator.
        if (string.IsNullOrEmpty(expected.CloudFilesRootIdentity))
            throw new InvalidOperationException("The Shell provider readback is empty and no expected Cloud Files root identity is retained.");
        var actual = readCloudFilesRoot();
        if (actual is null || actual.FileId == 0 || !SamePath(actual.Path, expected.SyncRootPath) ||
            !string.Equals(actual.ProviderName, MirrorPulseSyncRootRegistrationService.ProviderName, StringComparison.Ordinal) ||
            !string.Equals(actual.Identity, expected.CloudFilesRootIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("The empty Shell provider readback is not bound to the expected current Cloud Files registration.");
    }

    private static bool SamePath(string actual, string expected) =>
        Path.IsPathFullyQualified(actual) && Path.IsPathFullyQualified(expected) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);
}
