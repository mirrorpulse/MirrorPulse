using System.Collections.ObjectModel;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliTransferFixtureLayout
{
    private static CliTransferFixtureInventory AuthenticateCrossPackageInstances(string directory,
        CliTransferFixtureMarker marker, MirrorPulseAdapterTopology topology)
    {
        if (marker.Routes is not { Count: 2 } || topology.Roots.Count != 2 ||
            topology.Instances.Count != 2 || topology.Installations.Count != 2 ||
            topology.Roots.Select(value => value.RootId).Distinct().Count() != 2 ||
            topology.Instances.Select(value => value.InstanceId).Distinct().Count() != 2 ||
            topology.Installations.Select(value => value.InstallId).Distinct().Count() != 2 ||
            topology.Installations.Select(value => value.AdapterId).Distinct().Count() != 2 ||
            !Uri.TryCreate(marker.WebDavEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "http" || endpoint.Host != "localhost" || endpoint.Port <= 0 ||
            endpoint.AbsolutePath != "/dav/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidDataException("The cross-package fixture requires two single-root instances and its private loopback endpoint.");
        var sides = new Dictionary<string, RootRegistration>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>(); var instances = new HashSet<Guid>(); var installations = new HashSet<InstallId>();
        foreach (var expected in marker.Routes)
        {
            if (expected is null || expected.FixtureSide is not ("left" or "right") ||
                !Guid.TryParseExact(expected.RootId, "D", out Guid rootId) || rootId == Guid.Empty || !ids.Add(rootId) ||
                !Guid.TryParseExact(expected.InstanceId, "D", out Guid instanceId) || instanceId == Guid.Empty || !instances.Add(instanceId) ||
                !SafeName(expected.DirectoryName) || !names.Add(expected.DirectoryName))
                throw new InvalidDataException("The cross-package marker contains a foreign, duplicate or redirected route.");
            string key = expected.FixtureSide == "left" ? "local" : "webdav";
            string adapter = "com.mirrorpulse.adapter." + key;
            var installed = topology.Installations.SingleOrDefault(value => value.AdapterId.Value == adapter);
            var actual = topology.Roots.SingleOrDefault(value => value.RootId.Value == rootId);
            var instance = topology.Instances.SingleOrDefault(value => value.InstanceId.Value == instanceId);
            string configurationKey = "root." + key + (key == "local" ? ".sourceDirectory" : ".endpoint");
            string configurationValue = key == "local" ? Path.Combine(directory, "source", "left") : endpoint.AbsoluteUri;
            if (installed is null || !installed.IsSigned || !installations.Add(installed.InstallId) ||
                installed.Manifest.Protocol.Minimum != 2 || installed.Manifest.Protocol.Maximum != 2 ||
                installed.Manifest.RootDefinitions is not { Count: 1 } || installed.Manifest.RootDefinitions[0].Key != key ||
                expected.RootKey != key || actual is null || instance is null || actual.InstanceId != instance.InstanceId ||
                actual.UniquenessKey != key || actual.DirectoryName != expected.DirectoryName || actual.Label != expected.DirectoryName ||
                actual.AdapterId != installed.AdapterId || actual.IdentityScope != RootIdentityScope.InstanceRoot ||
                actual.State != RootRegistrationState.Active || instance.AdapterId != installed.AdapterId ||
                instance.InstallId != installed.InstallId || !instance.Enabled || instance.CredentialReferences.Count != 0 ||
                instance.Configuration.Count != 1 || !instance.Configuration.TryGetValue(configurationKey, out string? configured) ||
                !string.Equals(configured, configurationValue, key == "local" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !sides.TryAdd(expected.FixtureSide, actual))
                throw new InvalidDataException("The cross-package topology lost its signed declared roots, original instances or private backend configuration.");
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
