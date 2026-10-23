using System.Collections.ObjectModel;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliTransferFixtureLayout
{
    private static CliTransferFixtureInventory AuthenticateSingleRootInstances(string directory,
        CliTransferFixtureMarker marker, MirrorPulseAdapterTopology topology)
    {
        if (marker.Routes is not { Count: 2 } || topology.Roots.Count != 2 ||
            topology.Instances.Count != 2 || topology.Installations.Count != 1)
            throw new InvalidDataException("The single-root transfer fixture requires exactly two routes and two instances.");
        var installed = topology.Installations[0];
        if (installed.AdapterId.Value != "com.mirrorpulse.adapter.local" || !installed.IsSigned ||
            installed.Manifest.Protocol.Minimum != 2 || installed.Manifest.Protocol.Maximum != 2 ||
            installed.Manifest.RootDefinitions is not { Count: 1 } || installed.Manifest.RootDefinitions[0].Key != "local")
            throw new InvalidDataException("The single-root fixture requires the signed Local V2 manifest's actual local root.");
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
            if (actual is null || instance is null || actual.InstanceId != instance.InstanceId ||
                actual.UniquenessKey != "local" || actual.DirectoryName != expected.DirectoryName || actual.Label != expected.DirectoryName ||
                actual.AdapterId != installed.AdapterId || actual.IdentityScope != RootIdentityScope.InstanceRoot ||
                actual.State != RootRegistrationState.Active || instance.AdapterId != installed.AdapterId ||
                instance.InstallId != installed.InstallId || !instance.Enabled || instance.CredentialReferences.Count != 0 ||
                instance.Configuration.Count != 1 || !instance.Configuration.TryGetValue("root.local.sourceDirectory", out string? source) ||
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
        return new(Array.AsReadOnly(new[] { sides["left"], sides["right"] }), new ReadOnlyDictionary<string, bool>(allowed));
    }
}
