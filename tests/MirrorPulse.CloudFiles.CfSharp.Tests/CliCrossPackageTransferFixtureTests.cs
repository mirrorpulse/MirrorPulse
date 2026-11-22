using System.Net;
using System.Net.Http.Headers;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliCrossPackageTransferFixtureTests
{
    [TestMethod]
    public void TwoSignedDeclaredRootsAuthenticateOnlyTheirExactBackendConfiguration()
    {
        var fixture = Create();
        var actual = CliTransferFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology);
        Assert.HasCount(2, actual.Roots); Assert.HasCount(7, actual.AllowedEntries);
        Assert.AreEqual("local", actual.Roots[0].UniquenessKey); Assert.AreEqual("webdav", actual.Roots[1].UniquenessKey);
        Assert.AreNotEqual(actual.Roots[0].AdapterId, actual.Roots[1].AdapterId);
        Assert.IsFalse(actual.AllowedEntries.ContainsKey("Right/foreign.bin"));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    [TestMethod]
    [DataRow("remote-endpoint")]
    [DataRow("endpoint-credentials")]
    [DataRow("endpoint-path")]
    [DataRow("outside-source")]
    [DataRow("unsigned")]
    [DataRow("wrong-root")]
    [DataRow("extra-root")]
    [DataRow("extra-config")]
    [DataRow("disabled")]
    [DataRow("duplicate-instance")]
    public void ForeignTopologyCannotAuthorizeCrossPackageCleanup(string failure)
    {
        var fixture = Create(failure);
        Assert.ThrowsExactly<InvalidDataException>(() => CliTransferFixtureLayout.Authenticate(fixture.Path, fixture.Marker, fixture.Topology));
        Assert.IsFalse(Directory.Exists(fixture.Path));
    }

    private static (string Path, CliTransferFixtureMarker Marker, MirrorPulseAdapterTopology Topology) Create(string? failure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), CliTransferFixtureLayout.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        const string endpoint = "http://localhost:13579/dav/";
        var installations = new List<InstalledAdapter>(); var instances = new List<AdapterInstance>();
        var roots = new List<RootRegistration>(); var routes = new List<CliTransferFixtureRoute>();
        var shared = InstanceId.New();
        foreach (string side in new[] { "left", "right" })
        {
            string key = side == "left" ? "local" : "webdav", label = side == "left" ? "Left" : "Right";
            var adapter = AdapterId.Parse("com.mirrorpulse.adapter." + key);
            var definition = new AdapterRootDefinition(key, label, label, false);
            var manifest = new AdapterManifest(1, adapter, label, "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                failure == "extra-root" ? [definition, new("extra", "Extra", "Extra", false)] : [definition]);
            var installed = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed", side), new(new string('a', 64)),
                AdapterInstallSource.LocalFile, null, failure != "unsigned", DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            var instanceId = failure == "duplicate-instance" ? shared : InstanceId.New();
            var root = AdapterRootRegistrationMapper.Map(adapter, instanceId, definition,
                failure == "disabled" ? RootRegistrationState.Disabled : RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            var configuration = new Dictionary<string, string>
            {
                ["root." + key + (side == "left" ? ".sourceDirectory" : ".endpoint")] = side == "left" ?
                    failure == "outside-source" ? Path.GetTempPath() : Path.Combine(directory, "source", side) : endpoint
            };
            if (failure == "extra-config") configuration.Add("other", "value");
            instances.Add(new(adapter, installed.InstallId, instanceId, label, configuration, [], Path.Combine(directory, "files", side),
                Path.Combine(directory, "transfers", side), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow));
            installations.Add(installed); roots.Add(root);
            routes.Add(new(instanceId.ToString(), root.RootId.ToString(), failure == "wrong-root" ? "foreign" : key, label) { FixtureSide = side });
        }
        string markerEndpoint = failure switch
        {
            "remote-endpoint" => "https://example.invalid/dav/",
            "endpoint-credentials" => "http://user@localhost:13579/dav/",
            "endpoint-path" => "http://localhost:13579/outside/",
            _ => endpoint
        };
        return (directory, new(3, routes) { WebDavEndpoint = markerEndpoint }, new(installations, instances, roots));
    }

    [TestMethod]
    public async Task FileBackedHttpOriginEnforcesPinnedWholeAndRangeBytesAndConditionalMutations()
    {
        string fixture = Path.Combine(Path.GetTempPath(), CliTransferFixtureLayout.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture, "source", "right"); Directory.CreateDirectory(source);
        byte[] original = [1, 2, 3, 4, 5], replacement = [9, 8, 7];
        await File.WriteAllBytesAsync(Path.Combine(source, "reverse.bin"), original);
        try
        {
            await using var server = new CliTransferWebDavFixture(fixture);
            using var client = new HttpClient { BaseAddress = server.Endpoint, Timeout = TimeSpan.FromSeconds(10) };
            using var head = await client.SendAsync(new(HttpMethod.Head, "reverse.bin"));
            string tag = head.Headers.ETag!.Tag;
            using var range = new HttpRequestMessage(HttpMethod.Get, "reverse.bin"); range.Headers.IfMatch.ParseAdd(tag);
            range.Headers.Range = new RangeHeaderValue(1, 3);
            using var read = await client.SendAsync(range);
            Assert.AreEqual(HttpStatusCode.PartialContent, read.StatusCode);
            CollectionAssert.AreEqual(new byte[] { 2, 3, 4 }, await read.Content.ReadAsByteArrayAsync());
            using var stale = new HttpRequestMessage(HttpMethod.Put, "reverse.bin") { Content = new ByteArrayContent(replacement) };
            stale.Headers.IfMatch.ParseAdd("\"wrong\"");
            using var rejected = await client.SendAsync(stale); Assert.AreEqual(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(source, "reverse.bin")));
            using var create = new HttpRequestMessage(HttpMethod.Put, "forward-moved.bin") { Content = new ByteArrayContent(replacement) };
            create.Headers.IfNoneMatch.ParseAdd("*");
            using var created = await client.SendAsync(create); Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(Path.Combine(source, "forward-moved.bin")));
            using var delete = new HttpRequestMessage(HttpMethod.Delete, "reverse.bin"); delete.Headers.IfMatch.ParseAdd(tag);
            using var deleted = await client.SendAsync(delete); Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(source, "reverse.bin")));
            using var foreign = await client.PutAsync("foreign.bin", new ByteArrayContent(replacement));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode); Assert.IsFalse(File.Exists(Path.Combine(source, "foreign.bin")));
            Assert.AreEqual(1, server.Puts); Assert.AreEqual(1, server.Deletes); Assert.AreEqual(1, server.ConditionalRejections);
        }
        finally { Directory.Delete(CliTransferFixtureLayout.RequireFixtureDirectory(fixture), true); }
    }
}
