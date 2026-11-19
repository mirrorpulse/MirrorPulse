using System.Collections.ObjectModel;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliTransferFixtureLayout
{
    private static CliTransferFixtureInventory AuthenticateSingleRootInstances(string directory,
        CliTransferFixtureMarker marker, MirrorPulseAdapterTopology topology)
    {
        int expectedInstallations = marker.LegacySourceInstallId is null ? 1 : 2;
        if (marker.Routes is not { Count: 2 } || topology.Roots.Count != 2 || topology.Instances.Count != 2 ||
            topology.Installations.Count != expectedInstallations ||
            (marker.LegacySourceInstallId is null) != (marker.LegacySourcePackageSha256 is null))
            throw new InvalidDataException("The single-root transfer fixture requires exactly two routes and two instances.");
        var modern = topology.Installations.Where(item => item.InstallId.Value != marker.LegacySourceInstallId).ToArray();
        if (modern.Length != 1) throw new InvalidDataException("The fixture requires one original modern Local installation.");
        var installed = modern[0];
        if (installed.AdapterId.Value != "com.mirrorpulse.adapter.local" || !installed.IsSigned ||
            installed.Manifest.Protocol.Minimum != 2 || installed.Manifest.Protocol.Maximum != 2 ||
            installed.Manifest.RootDefinitions is not { Count: 1 } || installed.Manifest.RootDefinitions[0].Key != "local")
            throw new InvalidDataException("The single-root fixture requires the signed Local V2 manifest's actual local root.");
        if (marker.LegacySourceInstallId is { } legacyId)
        {
            var legacy = topology.Installations.Single(item => item.InstallId.Value == legacyId);
            string fixtureInstallations = Path.Combine(directory, "data", "adapters", "installed") + Path.DirectorySeparatorChar;
            if (!marker.LegacySourceConfiguration || legacyId == Guid.Empty || !legacy.IsSigned || legacy.AdapterId != installed.AdapterId ||
                legacy.Version != "0.1.3" || legacy.Manifest.Protocol.Minimum != 1 || legacy.Manifest.Protocol.Maximum != 1 ||
                legacy.Manifest.RootDefinitions is not { Count: 1 } || legacy.Manifest.RootDefinitions[0].Key != "local" ||
                !string.Equals(legacy.PackageSha256.Hexadecimal, marker.LegacySourcePackageSha256, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(legacy.InstallationDirectory).StartsWith(fixtureInstallations, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only the recorded signed Local V1 asset inside this fixture may remain unselected.");
        }
        var sides = new Dictionary<string, RootRegistration>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var instances = new HashSet<Guid>();
        foreach (var expected in marker.Routes)
        {
            if (expected is null || expected.RootKey != "local" || expected.FixtureSide is not ("left" or "right") ||
                !Guid.TryParseExact(expected.RootId, "D", out Guid rootId) || rootId == Guid.Empty || !ids.Add(rootId) ||
                !Guid.TryParseExact(expected.InstanceId, "D", out Guid instanceId) || instanceId == Guid.Empty || !instances.Add(instanceId) ||
                !SafeName(expected.DirectoryName) || !names.Add(expected.DirectoryName))
                throw new InvalidDataException("The single-root marker contains a foreign, duplicate or redirected route.");
            var actual = topology.Roots.SingleOrDefault(candidate => candidate.RootId.Value == rootId);
            var instance = topology.Instances.SingleOrDefault(candidate => candidate.InstanceId.Value == instanceId);
            string sourceDirectory = Path.Combine(directory, "source", expected.FixtureSide);
            string configurationKey = marker.LegacySourceConfiguration && expected.FixtureSide == "left"
                ? "sourceDirectory"
                : "root.local.sourceDirectory";
            if (actual is null || instance is null || actual.InstanceId != instance.InstanceId ||
                actual.UniquenessKey != "local" || actual.DirectoryName != expected.DirectoryName || actual.Label != expected.DirectoryName ||
                actual.AdapterId != installed.AdapterId || actual.IdentityScope != RootIdentityScope.InstanceRoot ||
                actual.State != RootRegistrationState.Active || instance.AdapterId != installed.AdapterId ||
                instance.InstallId != installed.InstallId || !instance.Enabled || instance.CredentialReferences.Count != 0 ||
                instance.Configuration.Count != 1 || !instance.Configuration.TryGetValue(configurationKey, out string? source) ||
                !Path.IsPathFullyQualified(source) || !string.Equals(source, sourceDirectory, StringComparison.OrdinalIgnoreCase) ||
                !sides.TryAdd(expected.FixtureSide, actual))
                throw new InvalidDataException("The single-root topology lost its exact declared root, instance or private source directory.");
        }
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = true };
        foreach (var side in sides.Values) allowed.Add(side.DirectoryName, true);
        allowed.Add(sides["left"].DirectoryName + "/forward.bin", false);
        allowed.Add(sides["left"].DirectoryName + "/reverse-moved.bin", false);
        allowed.Add(sides["right"].DirectoryName + "/reverse.bin", false);
        allowed.Add(sides["right"].DirectoryName + "/forward-moved.bin", false);
        if (marker.DirectoryTransferOperationId is { } directoryOperation)
        {
            if (directoryOperation == Guid.Empty || marker.TransferOperationIds?.Contains(directoryOperation) != false)
                throw new InvalidDataException("Directory transfer requires a distinct original operation ID.");
            foreach (string path in new[] { sides["left"].DirectoryName + "/transfer-tree", sides["right"].DirectoryName + "/transferred-tree" })
            {
                allowed.Add(path, true);
                allowed.Add(path + "/nested", true);
                allowed.Add(path + "/nested/payload.bin", false);
            }
        }
        return new(Array.AsReadOnly(new[] { sides["left"], sides["right"] }), new ReadOnlyDictionary<string, bool>(allowed));
    }
}
