using System.Collections.ObjectModel;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliTransferFixtureRoute(string InstanceId, string RootId, string RootKey, string DirectoryName)
{
    public string? FixtureSide { get; init; }
}
public sealed record CliTransferFixtureMarker(int SchemaVersion, IReadOnlyList<CliTransferFixtureRoute> Routes)
{
    public IReadOnlyList<Guid>? TransferOperationIds { get; init; }
}
public sealed record CliTransferFixtureInventory(IReadOnlyList<RootRegistration> Roots, IReadOnlyDictionary<string, bool> AllowedEntries);

/// <summary>Authenticates only the three synthetic routes used by the signed CLI transfer fixture.</summary>
/// <remarks>This test-only policy neither starts Cloud Files nor changes permissions. The caller must
/// hold the stopped Host's exclusive catalog lease before using the inventory for cleanup.</remarks>
public static partial class CliTransferFixtureLayout
{
    public const string DirectoryPrefix = "MirrorPulse-cli-transfer-";
    public const string MarkerFileName = ".mp-cli-transfer-fixture.json";

    public static CliTransferFixtureInventory Authenticate(string directory, CliTransferFixtureMarker marker,
        MirrorPulseAdapterTopology topology)
    {
        ArgumentNullException.ThrowIfNull(marker); ArgumentNullException.ThrowIfNull(topology);
        string root = RequireFixtureDirectory(directory);
        if (marker.SchemaVersion == 2) return AuthenticateSingleRootInstances(root, marker, topology);
        if (marker.SchemaVersion != 1 || marker.Routes is not { Count: 3 } || topology.Roots.Count != 3 ||
            topology.Instances.Count != 2 || topology.Installations.Count != 1)
            throw new InvalidDataException("Transfer fixture authentication requires its exact temporary directory and complete three-route marker.");
        var installed = topology.Installations[0];
        if (installed.AdapterId.Value != "com.mirrorpulse.adapter.local" || !installed.IsSigned ||
            installed.Manifest.Protocol.Minimum != 2 || installed.Manifest.Protocol.Maximum != 2)
            throw new InvalidDataException("Transfer fixture authentication requires its retained signed Local V2 installation.");
        var routes = new Dictionary<string, RootRegistration>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        foreach (var expected in marker.Routes)
        {
            if (expected is null || expected.RootKey is not ("left" or "right" or "target") ||
                !Guid.TryParseExact(expected.RootId, "D", out Guid rootId) || rootId == Guid.Empty || !ids.Add(rootId) ||
                !Guid.TryParseExact(expected.InstanceId, "D", out Guid instanceId) || instanceId == Guid.Empty ||
                !SafeName(expected.DirectoryName) || !names.Add(expected.DirectoryName))
                throw new InvalidDataException("The transfer fixture marker contains a foreign, duplicate or redirected route.");
            var actual = topology.Roots.SingleOrDefault(candidate => candidate.RootId.Value == rootId);
            if (actual is null || actual.InstanceId.Value != instanceId || actual.UniquenessKey != expected.RootKey ||
                actual.DirectoryName != expected.DirectoryName || actual.AdapterId != installed.AdapterId ||
                actual.IdentityScope != RootIdentityScope.InstanceRoot || actual.State != RootRegistrationState.Active ||
                !routes.TryAdd(expected.RootKey, actual))
                throw new InvalidDataException("The retained transfer topology disagrees with its exact original routes.");
        }
        if (routes.Count != 3 || routes["left"].InstanceId != routes["right"].InstanceId ||
            routes["left"].InstanceId == routes["target"].InstanceId)
            throw new InvalidDataException("Transfer fixture routes require one shared source instance and a distinct target instance.");
        foreach (var instance in topology.Instances)
        {
            string[] keys = routes.Values.Where(route => route.InstanceId == instance.InstanceId).Select(route => route.UniquenessKey).ToArray();
            if (keys.Length == 0 || instance.AdapterId != installed.AdapterId || instance.InstallId != installed.InstallId ||
                !instance.Enabled || instance.CredentialReferences.Count != 0 || instance.Configuration.Count != keys.Length)
                throw new InvalidDataException("An unrelated instance or configuration cannot authorize transfer fixture cleanup.");
            foreach (string key in keys)
            {
                string expected = Path.Combine(root, "source", key);
                if (!instance.Configuration.TryGetValue("root." + key + ".sourceDirectory", out string? source) ||
                    !Path.IsPathFullyQualified(source) || !string.Equals(source, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The transfer fixture source must be its exact private synthetic directory.");
            }
        }
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = true };
        foreach (var route in routes.Values) allowed.Add(route.DirectoryName, true);
        allowed.Add(routes["left"].DirectoryName + "/same-instance.bin", false);
        allowed.Add(routes["left"].DirectoryName + "/cross-instance.bin", false);
        allowed.Add(routes["right"].DirectoryName + "/same-instance-moved.bin", false);
        allowed.Add(routes["target"].DirectoryName + "/cross-instance-moved.bin", false);
        return new(Array.AsReadOnly(routes.Values.OrderBy(route => route.UniquenessKey, StringComparer.Ordinal).ToArray()),
            new ReadOnlyDictionary<string, bool>(allowed));
    }

    public static string RequireFixtureDirectory(string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _))
            throw new InvalidDataException("Transfer fixture authentication requires its exact temporary GUID directory.");
        return root;
    }

    private static bool SafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
        name is not ("." or "..") && name == name.TrimEnd(' ', '.') && !name.Contains('/') && !name.Contains('\\') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
