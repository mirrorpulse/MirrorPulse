using System.Collections.ObjectModel;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliDisabledFixtureMember(Guid MemberId, Guid ItemId);
public sealed record CliDisabledFixtureMarker(int SchemaVersion, string SourceSha,
    IReadOnlyList<CliTransferFixtureRoute> Routes, IReadOnlyDictionary<string, Guid> Commands)
{
    public string Phase { get; init; } = "Setup";
    public IReadOnlyDictionary<string, CliDisabledFixtureMember> Members { get; init; } = new Dictionary<string, CliDisabledFixtureMember>();
    public IReadOnlyList<Guid> Originals { get; init; } = [];
}
public sealed record CliDisabledFixtureMemberSpec(string Side, string Name, bool IsDirectory);
public sealed record CliDisabledFixtureInventory(IReadOnlyList<RootRegistration> Roots,
    IReadOnlyDictionary<string, bool> AllowedEntries);

/// <summary>Authenticates only the two signed Local instances and fixed names of the disposable offline-operation fixture.</summary>
public static class CliDisabledFixtureLayout
{
    public const string DirectoryPrefix = "MirrorPulse-cli-disabled-";
    public const string MarkerFileName = ".mp-cli-disabled-fixture.json";

    public static IReadOnlyList<string> CommandNames { get; } = Array.AsReadOnly(new[]
    {
        "createBornFile", "createBornDirectory", "createEphemeralFile", "createEphemeralDirectory",
        "moveKeptFile", "moveBornFile", "moveBornDirectory", "replaceKeptFile", "retireFile",
        "retireDirectory", "retireEphemeralFile", "retireEphemeralDirectory"
    });

    public static IReadOnlyDictionary<string, CliDisabledFixtureMemberSpec> MemberSpecs { get; } =
        new ReadOnlyDictionary<string, CliDisabledFixtureMemberSpec>(new Dictionary<string, CliDisabledFixtureMemberSpec>(StringComparer.Ordinal)
        {
            ["keptFile"] = new("offline", "kept.txt", false),
            ["bornFile"] = new("offline", "born.txt", false),
            ["bornDirectory"] = new("offline", "born-dir", true),
            ["retiredFile"] = new("offline", "retire.bin", false),
            ["retiredDirectory"] = new("offline", "retire-dir", true),
            ["ephemeralFile"] = new("offline", "ephemeral.bin", false),
            ["ephemeralDirectory"] = new("offline", "ephemeral-dir", true),
            ["metadataFile"] = new("offline", "metadata.txt", false),
            ["activeFile"] = new("active", "progress.txt", false)
        });

    public static string RequireFixtureDirectory(string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _))
            throw new InvalidDataException("Disabled fixture authentication requires its exact temporary GUID directory.");
        return root;
    }

    /// <summary>Normalizes only native separator spelling before the exact fixed-name lookup.</summary>
    public static string RequireAllowedRelativePath(CliDisabledFixtureInventory inventory, string path)
    {
        ArgumentNullException.ThrowIfNull(inventory); ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Replace('\\', '/');
        if (!inventory.AllowedEntries.ContainsKey(normalized))
            throw new InvalidDataException("The disabled oracle path is outside its exact fixed inventory.");
        return normalized;
    }

    public static CliDisabledFixtureInventory Authenticate(string directory, CliDisabledFixtureMarker marker, MirrorPulseAdapterTopology topology)
    {
        ArgumentNullException.ThrowIfNull(marker); ArgumentNullException.ThrowIfNull(topology);
        string root = RequireFixtureDirectory(directory);
        if (marker.SchemaVersion != 1 || marker.SourceSha is not { Length: 40 } || !marker.SourceSha.All(char.IsAsciiHexDigit) ||
            marker.Phase is not ("Setup" or "Before" or "DisabledRestart" or "ActiveProgress" or "Reenabled") ||
            marker.Routes is not { Count: 2 } || topology.Installations.Count != 1 || topology.Instances.Count != 2 || topology.Roots.Count != 2 ||
            marker.Commands is null || marker.Commands.Count != CommandNames.Count ||
            CommandNames.Any(name => !marker.Commands.TryGetValue(name, out Guid id) || id == Guid.Empty) ||
            marker.Commands.Values.Distinct().Count() != CommandNames.Count || marker.Members is null || marker.Members.Count > MemberSpecs.Count ||
            marker.Members.Any(item => !MemberSpecs.ContainsKey(item.Key) || item.Value is null || item.Value.MemberId == Guid.Empty || item.Value.ItemId == Guid.Empty) ||
            marker.Members.Values.Select(item => item.MemberId).Distinct().Count() != marker.Members.Count ||
            marker.Members.Values.Select(item => item.ItemId).Distinct().Count() != marker.Members.Count ||
            marker.Originals is null || marker.Originals.Count > 256 || marker.Originals.Any(id => id == Guid.Empty) || marker.Originals.Distinct().Count() != marker.Originals.Count)
            throw new InvalidDataException("The disabled fixture lost its exact bounded marker, commands or topology.");
        var installed = topology.Installations[0];
        if (installed.AdapterId.Value != "com.mirrorpulse.adapter.local" || !installed.IsSigned ||
            installed.Manifest.Protocol.Minimum != 2 || installed.Manifest.Protocol.Maximum != 2 ||
            installed.Manifest.RootDefinitions is not { Count: 1 } || installed.Manifest.RootDefinitions[0].Key != "local")
            throw new InvalidDataException("The disabled fixture requires its retained signed Local V2 single-root installation.");
        var sides = new Dictionary<string, RootRegistration>(StringComparer.Ordinal);
        var rootIds = new HashSet<Guid>(); var instanceIds = new HashSet<Guid>();
        foreach (var route in marker.Routes)
        {
            if (route is null || route.FixtureSide is not ("offline" or "active") || route.RootKey != "local" ||
                !Guid.TryParseExact(route.RootId, "D", out Guid rootId) || rootId == Guid.Empty || !rootIds.Add(rootId) ||
                !Guid.TryParseExact(route.InstanceId, "D", out Guid instanceId) || instanceId == Guid.Empty || !instanceIds.Add(instanceId))
                throw new InvalidDataException("The disabled fixture contains a foreign or duplicate route.");
            string label = route.FixtureSide == "offline" ? "Offline Operations" : "Active Progress";
            var actual = topology.Roots.SingleOrDefault(item => item.RootId.Value == rootId);
            var instance = topology.Instances.SingleOrDefault(item => item.InstanceId.Value == instanceId);
            string sourceDirectory = Path.Combine(root, "source", route.FixtureSide);
            if (actual is null || instance is null || route.DirectoryName != label || actual.DirectoryName != label || actual.Label != label ||
                actual.InstanceId != instance.InstanceId || actual.AdapterId != installed.AdapterId || actual.UniquenessKey != "local" ||
                actual.IdentityScope != RootIdentityScope.InstanceRoot || actual.CustomEntry ||
                actual.State != (instance.Enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled) ||
                instance.AdapterId != installed.AdapterId || instance.InstallId != installed.InstallId || instance.CredentialReferences.Count != 0 ||
                instance.Configuration.Count != 1 || !instance.Configuration.TryGetValue("root.local.sourceDirectory", out string? source) ||
                !Path.IsPathFullyQualified(source) || !string.Equals(source, sourceDirectory, StringComparison.OrdinalIgnoreCase) ||
                !sides.TryAdd(route.FixtureSide, actual))
                throw new InvalidDataException("The disabled fixture lost its exact private source, instance or declared root state.");
        }
        if (sides.Count != 2) throw new InvalidDataException("Both distinct single-root instances are required.");
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = true };
        foreach (var item in sides.Values) allowed.Add(item.DirectoryName, true);
        foreach (var spec in MemberSpecs.Values) allowed.Add(sides[spec.Side].DirectoryName + "/" + spec.Name, spec.IsDirectory);
        foreach (var item in new[] { new CliDisabledFixtureMemberSpec("offline", "kept-renamed.txt", false),
            new("offline", "born-renamed.txt", false), new("offline", "moved-dir", true) })
            allowed.Add(sides[item.Side].DirectoryName + "/" + item.Name, item.IsDirectory);
        return new(Array.AsReadOnly(new[] { sides["offline"], sides["active"] }), new ReadOnlyDictionary<string, bool>(allowed));
    }
}
