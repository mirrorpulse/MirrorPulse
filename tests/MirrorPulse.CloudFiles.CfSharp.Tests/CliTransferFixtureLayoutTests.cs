using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliTransferFixtureLayoutTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void CompleteSyntheticLayoutAuthenticatesOnlyTheOriginalThreeRoutesAndEightNames()
    {
        var fixture = Create();
        var inventory = CliTransferFixtureLayout.Authenticate(fixture.Root, fixture.Marker, fixture.Topology);
        Assert.HasCount(3, inventory.Roots); Assert.HasCount(8, inventory.AllowedEntries);
        Assert.IsTrue(inventory.AllowedEntries[""]); Assert.IsTrue(inventory.AllowedEntries["Left"]);
        Assert.IsFalse(inventory.AllowedEntries["Left/same-instance.bin"]);
        Assert.IsFalse(inventory.AllowedEntries["Right/same-instance-moved.bin"]);
        Assert.IsFalse(inventory.AllowedEntries["Target/cross-instance-moved.bin"]);
        Assert.IsFalse(inventory.AllowedEntries.ContainsKey("Left/unrelated.txt"));
        Assert.IsFalse(Directory.Exists(fixture.Root), "Authentication alone must not create a catalog or start Cloud Files.");
    }

    [TestMethod]
    [DataRow("foreign-directory")]
    [DataRow("invalid-guid-directory")]
    [DataRow("schema")]
    [DataRow("missing-route")]
    [DataRow("duplicate-id")]
    [DataRow("duplicate-directory")]
    [DataRow("escaped-directory")]
    [DataRow("normalized-directory")]
    [DataRow("foreign-instance")]
    [DataRow("foreign-key")]
    [DataRow("source-outside-fixture")]
    [DataRow("unknown-configuration")]
    [DataRow("same-instance-target")]
    [DataRow("disabled-root")]
    [DataRow("unsigned")]
    [DataRow("legacy-protocol")]
    [DataRow("foreign-adapter")]
    public void ForeignIncompleteOrChangedLayoutCannotAuthorizeCleanup(string failure)
    {
        var fixture = Create(failure); var marker = fixture.Marker; string root = fixture.Root;
        var routes = marker.Routes.ToArray();
        switch (failure)
        {
            case "foreign-directory": root = Path.Combine(root, "nested"); break;
            case "invalid-guid-directory": root = Path.Combine(Path.GetTempPath(), CliTransferFixtureLayout.DirectoryPrefix + "unrelated"); break;
            case "schema": marker = marker with { SchemaVersion = 2 }; break;
            case "missing-route": marker = marker with { Routes = routes[..2] }; break;
            case "duplicate-id": routes[1] = routes[1] with { RootId = routes[0].RootId }; break;
            case "duplicate-directory": routes[1] = routes[1] with { DirectoryName = routes[0].DirectoryName.ToUpperInvariant() }; break;
            case "escaped-directory": routes[0] = routes[0] with { DirectoryName = "../outside" }; break;
            case "normalized-directory": routes[0] = routes[0] with { DirectoryName = "Left." }; break;
            case "foreign-instance": routes[0] = routes[0] with { InstanceId = Guid.NewGuid().ToString("D") }; break;
            case "foreign-key": routes[0] = routes[0] with { RootKey = "other" }; break;
        }
        if (failure is not ("schema" or "missing-route")) marker = marker with { Routes = routes };
        Assert.ThrowsExactly<InvalidDataException>(() => CliTransferFixtureLayout.Authenticate(root, marker, fixture.Topology));
        Assert.IsFalse(Directory.Exists(fixture.Root), "Rejection must precede native construction, permission writes and filesystem changes.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferCleanupRetainsExclusiveCatalogOwnershipAndNeverTouchesAnUnprotectedFixture(bool liveOwner)
    {
        var fixture = Create();
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture.Root, "sync"), Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        MirrorPulseProductCatalog? catalog = null;
        try
        {
            catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.SaveAdapterTopologyAsync(fixture.Topology);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliTransferFixtureLayout.MarkerFileName),
                JsonSerializer.Serialize(fixture.Marker, JsonOptions));
            string sentinel = Path.Combine(paths.SyncRootPath, "unrelated.txt");
            await File.WriteAllTextAsync(sentinel, "retain ordinary fixture bytes");
            byte[] access = new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorBinaryForm();
            if (!liveOwner) { await catalog.DisposeAsync(); catalog = null; }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, "--cleanup-cli-transfer-fixture", fixture.Root })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(liveOwner ? 1 : 0, process.ExitCode, await error);
            if (liveOwner)
            {
                Assert.AreEqual(string.Empty, (await output).Trim());
                StringAssert.Contains(await error, "stage=CatalogOwnership");
            }
            else
            {
                var result = JsonSerializer.Deserialize<CliFixtureCleanupResult>(await output, JsonOptions)!;
                Assert.IsTrue(result.ExclusiveCatalogOwner); Assert.IsFalse(result.ProtectedNamespace);
                Assert.AreEqual(0, result.ValidatedObjects); Assert.AreEqual(0, result.RestoredObjects); Assert.IsFalse(result.CloudRootUnregistered);
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")), "An unprotected or live-owned fixture must not start Cloud Files.");
            Assert.AreEqual("retain ordinary fixture bytes", await File.ReadAllTextAsync(sentinel));
            CollectionAssert.AreEqual(access, new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorBinaryForm());
        }
        finally
        {
            if (catalog is not null) await catalog.DisposeAsync();
            Directory.Delete(CliTransferFixtureLayout.RequireFixtureDirectory(fixture.Root), recursive: true);
        }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("empty")]
    [DataRow("duplicate")]
    [DataRow("unknown")]
    public async Task TransferOracleCannotInventCompletionFromMissingCommandsOrMatchingFiles(string failure)
    {
        var fixture = Create();
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture.Root, "sync"), Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths)) await catalog.SaveAdapterTopologyAsync(fixture.Topology);
            Guid first = Guid.NewGuid();
            IReadOnlyList<Guid>? operations = failure switch
            {
                "missing" => null,
                "empty" => [Guid.Empty, first],
                "duplicate" => [first, first],
                _ => [first, Guid.NewGuid()]
            };
            var marker = fixture.Marker with { TransferOperationIds = operations };
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliTransferFixtureLayout.MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
            string sentinel = Path.Combine(paths.SyncRootPath, "matching-target.bin");
            await File.WriteAllBytesAsync(sentinel, [1, 2, 3]);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, "--audit-cli-transfer-fixture", fixture.Root }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(1, process.ExitCode, await error); Assert.AreEqual(string.Empty, (await output).Trim());
            StringAssert.Contains(await error, "stage=TransferReceipts");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath), "The receipt oracle must not create or infer an official SDK store.");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(sentinel));
        }
        finally { Directory.Delete(CliTransferFixtureLayout.RequireFixtureDirectory(fixture.Root), recursive: true); }
    }

    private static (string Root, CliTransferFixtureMarker Marker, MirrorPulseAdapterTopology Topology) Create(string? failure = null)
    {
        string root = Path.Combine(Path.GetTempPath(), CliTransferFixtureLayout.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        AdapterId adapter = AdapterId.Parse(failure == "foreign-adapter" ? "example.other" : "com.mirrorpulse.adapter.local");
        var manifest = new AdapterManifest(1, adapter, "Transfer fixture", "1.0.0", failure == "legacy-protocol" ? new(1, 1) : new(2, 2),
            new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" }, new(null), new(null, null),
            new(true, false, true, true), ["en-US"], "1.0.0", []);
        var installed = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"), new(new string('a', 64)),
            AdapterInstallSource.LocalFile, null, failure != "unsigned", DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        InstanceId first = InstanceId.New(), second = failure == "same-instance-target" ? first : InstanceId.New();
        AdapterInstance Instance(InstanceId id, string[] keys)
        {
            var configuration = keys.ToDictionary(key => "root." + key + ".sourceDirectory", key => Path.Combine(root, "source", key));
            if (failure == "source-outside-fixture") configuration["root." + keys[0] + ".sourceDirectory"] = Path.GetTempPath();
            if (failure == "unknown-configuration") configuration["unrelated"] = "unexpected";
            return new(adapter, installed.InstallId, id, "Transfer fixture", configuration, [], Path.Combine(root, "files", id.ToString()),
                Path.Combine(root, "transfers", id.ToString()), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        }
        RootRegistration Route(InstanceId id, string key, string name) => new(adapter, id, RootId.New(), key, name, name, false,
            failure == "disabled-root" ? RootRegistrationState.Disabled : RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        RootRegistration[] roots = [Route(first, "left", "Left"), Route(first, "right", "Right"), Route(second, "target", "Target")];
        var marker = new CliTransferFixtureMarker(1, roots.Select(route => new CliTransferFixtureRoute(route.InstanceId.ToString(),
            route.RootId.ToString(), route.UniquenessKey, route.DirectoryName)).ToArray());
        return (root, marker, new([installed], [Instance(first, ["left", "right"]), Instance(second, ["target"])], roots));
    }
}
