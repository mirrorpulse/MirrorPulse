using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliSingleRootTransferFixtureLayoutTests
{
    [TestMethod]
    public void DeclaredLocalRootsInDistinctInstancesAuthenticateBothDirectionsWithoutCreatingFiles()
    {
        var fixture = Create();
        var actual = CliTransferFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology);
        Assert.HasCount(2, actual.Roots); Assert.HasCount(7, actual.AllowedEntries);
        Assert.AreNotEqual(actual.Roots[0].InstanceId, actual.Roots[1].InstanceId);
        Assert.IsTrue(actual.Roots.All(root => root.UniquenessKey == "local"));
        foreach (string file in new[] { "Left/forward.bin", "Right/forward-moved.bin", "Right/reverse.bin", "Left/reverse-moved.bin" })
            Assert.IsFalse(actual.AllowedEntries[file]);
        Assert.IsFalse(actual.AllowedEntries.ContainsKey("Left/unrelated.txt"));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("missing-route")]
    [DataRow("duplicate-root")]
    [DataRow("duplicate-instance")]
    [DataRow("duplicate-side")]
    [DataRow("foreign-key")]
    [DataRow("foreign-name")]
    [DataRow("outside-source")]
    [DataRow("extra-config")]
    [DataRow("disabled")]
    [DataRow("unsigned")]
    [DataRow("wrong-manifest")]
    [DataRow("multi-root-manifest")]
    public void ForeignOrUndeclaredSingleRootTopologyCannotAuthorizeCleanup(string failure)
    {
        var fixture = Create(failure); var marker = fixture.Marker; var routes = marker.Routes.ToArray();
        switch (failure)
        {
            case "schema": marker = marker with { SchemaVersion = 3 }; break;
            case "missing-route": marker = marker with { Routes = routes[..1] }; break;
            case "duplicate-root": routes[1] = routes[1] with { RootId = routes[0].RootId }; break;
            case "duplicate-instance": routes[1] = routes[1] with { InstanceId = routes[0].InstanceId }; break;
            case "duplicate-side": routes[1] = routes[1] with { FixtureSide = "left" }; break;
            case "foreign-key": routes[1] = routes[1] with { RootKey = "right" }; break;
            case "foreign-name": routes[1] = routes[1] with { DirectoryName = "../outside" }; break;
        }
        if (failure is not ("schema" or "missing-route")) marker = marker with { Routes = routes };
        Assert.ThrowsExactly<InvalidDataException>(() => CliTransferFixtureLayout.Authenticate(fixture.Path, marker, fixture.Topology));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    private static (string Path, CliTransferFixtureMarker Marker, MirrorPulseAdapterTopology Topology) Create(string? failure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), CliTransferFixtureLayout.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        var adapter = AdapterId.Parse("com.mirrorpulse.adapter.local");
        var definition = new AdapterRootDefinition(failure == "wrong-manifest" ? "other" : "local", "Local", "Local", false);
        var manifest = new AdapterManifest(1, adapter, "Local", "1.0.0", new(2, 2),
            new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
            failure == "multi-root-manifest" ? [definition, new("other", "Other", "Other", false)] : [definition]);
        var installed = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"), new(new string('a', 64)),
            AdapterInstallSource.LocalFile, null, failure != "unsigned", DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var roots = new List<RootRegistration>(); var instances = new List<AdapterInstance>(); var routes = new List<CliTransferFixtureRoute>();
        foreach (string side in new[] { "left", "right" })
        {
            var instanceId = InstanceId.New(); string label = side == "left" ? "Left" : "Right";
            var root = AdapterRootRegistrationMapper.Map(adapter, instanceId, definition, RootRegistrationState.Active,
                identityScope: RootIdentityScope.InstanceRoot);
            root = new(adapter, instanceId, root.RootId, "local", label, label, false,
                failure == "disabled" ? RootRegistrationState.Disabled : RootRegistrationState.Active, root.RegisteredAt, RootIdentityScope.InstanceRoot);
            var configuration = new Dictionary<string, string>
            {
                ["root.local.sourceDirectory"] =
                failure == "outside-source" ? Path.GetTempPath() : Path.Combine(directory, "source", side)
            };
            if (failure == "extra-config") configuration.Add("unrelated", "unexpected");
            instances.Add(new(adapter, installed.InstallId, instanceId, label, configuration, [], Path.Combine(directory, "files", side),
                Path.Combine(directory, "transfers", side), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow));
            roots.Add(root);
            routes.Add(new(instanceId.ToString(), root.RootId.ToString(), "local", label) { FixtureSide = side });
        }
        return (directory, new(2, routes.AsReadOnly()), new([installed], instances, roots));
    }
}
