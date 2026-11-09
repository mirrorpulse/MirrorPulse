using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliDisabledFixtureLayoutTests
{
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void ExactPrivateInstancesAuthenticateCurrentEnabledOrDisabledStateWithoutCreatingFiles(bool offlineEnabled, bool activeEnabled)
    {
        var fixture = Create(offlineEnabled, activeEnabled);
        var inventory = CliDisabledFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology);
        Assert.HasCount(2, inventory.Roots); Assert.HasCount(15, inventory.AllowedEntries);
        Assert.AreNotEqual(inventory.Roots[0].InstanceId, inventory.Roots[1].InstanceId);
        Assert.IsFalse(inventory.AllowedEntries["Offline Operations/kept-renamed.txt"]);
        Assert.IsTrue(inventory.AllowedEntries["Offline Operations/moved-dir"]);
        Assert.IsFalse(inventory.AllowedEntries.ContainsKey("Offline Operations/unrelated.txt"));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    [DataRow("Offline Operations/kept.txt")]
    [DataRow("Offline Operations\\kept.txt")]
    public void OfficialNativeSeparatorSpellingsResolveToTheSameExactFixedEntry(string path)
    {
        var fixture = Create(false, true);
        var inventory = CliDisabledFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology);
        Assert.AreEqual("Offline Operations/kept.txt", CliDisabledFixtureLayout.RequireAllowedRelativePath(inventory, path));
    }

    [TestMethod]
    [DataRow("Offline Operations/../kept.txt")]
    [DataRow("Offline Operations//kept.txt")]
    [DataRow("Other/kept.txt")]
    [DataRow("offline operations/kept.txt")]
    [DataRow("C:\\Offline Operations\\kept.txt")]
    public void SeparatorNormalizationCannotAdoptTraversalOtherRootsCaseAliasesOrAbsolutePaths(string path)
    {
        var fixture = Create(false, true);
        var inventory = CliDisabledFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology);
        Assert.ThrowsExactly<InvalidDataException>(() => CliDisabledFixtureLayout.RequireAllowedRelativePath(inventory, path));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("source")]
    [DataRow("phase")]
    [DataRow("missing-route")]
    [DataRow("duplicate-root")]
    [DataRow("duplicate-instance")]
    [DataRow("duplicate-side")]
    [DataRow("foreign-name")]
    [DataRow("foreign-key")]
    [DataRow("missing-command")]
    [DataRow("duplicate-command")]
    [DataRow("empty-command")]
    [DataRow("foreign-member")]
    [DataRow("duplicate-member")]
    [DataRow("aliased-item")]
    [DataRow("duplicate-original")]
    [DataRow("outside-source")]
    [DataRow("extra-config")]
    [DataRow("state-mismatch")]
    [DataRow("unsigned")]
    [DataRow("multi-root-manifest")]
    public void ForeignOrAmbiguousFixtureFactsCannotAuthorizeItsCleanup(string damage)
    {
        var fixture = Create(false, true, damage); var marker = fixture.Marker; var routes = marker.Routes.ToArray();
        switch (damage)
        {
            case "schema": marker = marker with { SchemaVersion = 2 }; break;
            case "source": marker = marker with { SourceSha = new string('z', 40) }; break;
            case "phase": marker = marker with { Phase = "Unknown" }; break;
            case "missing-route": marker = marker with { Routes = routes[..1] }; break;
            case "duplicate-root": routes[1] = routes[1] with { RootId = routes[0].RootId }; break;
            case "duplicate-instance": routes[1] = routes[1] with { InstanceId = routes[0].InstanceId }; break;
            case "duplicate-side": routes[1] = routes[1] with { FixtureSide = "offline" }; break;
            case "foreign-name": routes[1] = routes[1] with { DirectoryName = "../outside" }; break;
            case "foreign-key": routes[1] = routes[1] with { RootKey = "other" }; break;
            case "missing-command": marker = marker with { Commands = marker.Commands.Skip(1).ToDictionary() }; break;
            case "duplicate-command":
            case "empty-command":
                var commands = marker.Commands.ToDictionary();
                commands[CliDisabledFixtureLayout.CommandNames[1]] = damage == "empty-command" ? Guid.Empty : commands[CliDisabledFixtureLayout.CommandNames[0]];
                marker = marker with { Commands = commands }; break;
            case "foreign-member": marker = marker with { Members = new Dictionary<string, CliDisabledFixtureMember> { ["foreign"] = new(Guid.NewGuid(), Guid.NewGuid()) } }; break;
            case "duplicate-member":
            case "aliased-item":
                Guid member = Guid.NewGuid(), item = Guid.NewGuid();
                marker = marker with
                {
                    Members = new Dictionary<string, CliDisabledFixtureMember>
                    { ["keptFile"] = new(member, item), ["bornFile"] = new(damage == "duplicate-member" ? member : Guid.NewGuid(), damage == "aliased-item" ? item : Guid.NewGuid()) }
                }; break;
            case "duplicate-original": Guid original = Guid.NewGuid(); marker = marker with { Originals = [original, original] }; break;
        }
        if (damage is not ("schema" or "source" or "phase" or "missing-route")) marker = marker with { Routes = routes };
        Assert.ThrowsExactly<InvalidDataException>(() => CliDisabledFixtureLayout.Authenticate(fixture.Path, marker, fixture.Topology));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    private static (string Path, CliDisabledFixtureMarker Marker, MirrorPulseAdapterTopology Topology) Create(bool offlineEnabled, bool activeEnabled, string? damage = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), CliDisabledFixtureLayout.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        var adapter = AdapterId.Parse("com.mirrorpulse.adapter.local");
        var definition = new AdapterRootDefinition("local", "Local", "Local", false);
        var manifest = new AdapterManifest(1, adapter, "Local", "1.0.0", new(2, 2),
            new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
            damage == "multi-root-manifest" ? [definition, new("other", "Other", "Other", false)] : [definition]);
        var installed = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"), new(new string('a', 64)),
            AdapterInstallSource.LocalFile, null, damage != "unsigned", DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var roots = new List<RootRegistration>(); var instances = new List<AdapterInstance>(); var routes = new List<CliTransferFixtureRoute>();
        foreach (string side in new[] { "offline", "active" })
        {
            bool enabled = side == "offline" ? offlineEnabled : activeEnabled;
            string label = side == "offline" ? "Offline Operations" : "Active Progress";
            var instance = InstanceId.New();
            var mapped = AdapterRootRegistrationMapper.Map(adapter, instance, definition, RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            var root = new RootRegistration(adapter, instance, mapped.RootId, "local", label, label, false,
                damage == "state-mismatch" || enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled, mapped.RegisteredAt, RootIdentityScope.InstanceRoot);
            var configuration = new Dictionary<string, string>
            { ["root.local.sourceDirectory"] = damage == "outside-source" ? Path.GetTempPath() : Path.Combine(directory, "source", side) };
            if (damage == "extra-config") configuration.Add("unrelated", "unexpected");
            instances.Add(new(adapter, installed.InstallId, instance, label, configuration, [], Path.Combine(directory, "files", side),
                Path.Combine(directory, "transfers", side), enabled, enabled ? AdapterLifecycleState.Enabled : AdapterLifecycleState.Disabled, null, DateTimeOffset.UtcNow));
            roots.Add(root); routes.Add(new(instance.ToString(), root.RootId.ToString(), "local", label) { FixtureSide = side });
        }
        return (directory, new(1, new string('a', 40), routes.AsReadOnly(), CliDisabledFixtureLayout.CommandNames.ToDictionary(name => name, _ => Guid.NewGuid())),
            new([installed], instances, roots));
    }
}
