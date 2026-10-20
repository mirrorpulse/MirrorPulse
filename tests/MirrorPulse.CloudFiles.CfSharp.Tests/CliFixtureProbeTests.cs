using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CliFixtureProbeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("S-1-5-18", false)]
    [DataRow("S-1-5-18", true)]
    [DataRow("S-1-5-21-101-102-103-1001", false)]
    [DataRow("S-1-5-21-101-102-103-1001", true)]
    public void CleanupDescriptorRetainsOneOwnerAndPassesTheProductionCanonicalAccessBoundary(string sid, bool directory)
    {
        var owner = new SecurityIdentifier(sid);
        string access = CliFixtureProbe.CreateCleanupAccessDescriptor(owner, directory);
        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(access, access));
        var descriptor = new RawSecurityDescriptor(access);
        Assert.IsNull(descriptor.Owner); Assert.IsNull(descriptor.Group); Assert.IsNull(descriptor.SystemAcl);
        Assert.IsTrue(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.HasCount(1, descriptor.DiscretionaryAcl!);
        var ace = (CommonAce)descriptor.DiscretionaryAcl![0];
        Assert.AreEqual(owner, ace.SecurityIdentifier); Assert.AreEqual(AceQualifier.AccessAllowed, ace.AceQualifier);
        Assert.AreEqual((int)FileSystemRights.FullControl, ace.AccessMask);
        Assert.AreEqual(directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None, ace.AceFlags);
    }

    [TestMethod]
    public async Task AnchorOnlyCleanupRequiresAnEmptyOwnedCatalogAndDoesNotRelaxOrdinaryPermissions()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-cli-integration-" + Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths)) Assert.IsEmpty((await catalog.ReadAdapterTopologyAsync()).Roots);
            var marker = new CliFixtureMarker(2, string.Empty, string.Empty, string.Empty, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(root, CliFixtureProbe.MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
            var result = await RunAsync(root, audit: false, cleanup: true);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            var cleanup = JsonSerializer.Deserialize<CliFixtureCleanupResult>(result.Output, JsonOptions)!;
            Assert.IsTrue(cleanup.ExclusiveCatalogOwner); Assert.IsFalse(cleanup.ProtectedNamespace); Assert.AreEqual(0, cleanup.RestoredObjects);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "inspection")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("read")]
    [DataRow("audit")]
    [DataRow("inspect")]
    [DataRow("cleanup-with-route")]
    public async Task AnchorOnlyMarkerCannotAuthorizeAReadAuditInspectionOrConfiguredNamespaceCleanup(string mode)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName),
            JsonSerializer.Serialize(new CliFixtureMarker(2, string.Empty, string.Empty, string.Empty, string.Empty), JsonOptions));
        var result = await RunAsync(fixture.Root, audit: mode == "audit", inspect: mode == "inspect", cleanup: mode == "cleanup-with-route");
        Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Acknowledged)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.Conflict)]
    public async Task AuditCountsReadOnlyContentIntentsEvenAfterAcknowledgement(MirrorPulseMutationState state)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await SeedAsync(catalog, fixture.Marker, "nested/fixture.bin", state);
            await SeedAsync(catalog, fixture.Marker, "queued-upload.txt", MirrorPulseMutationState.Acknowledged);
            await catalog.PrepareMutationAsync(new(Guid.NewGuid(), InstanceId.Parse(fixture.Marker.InstanceId), fixture.Marker.RootKey,
                MirrorPulseWorkerChangeKind.Create, "nested", null, true, null, null, null, MirrorPulseMutationOrigin.Rescan));
        }
        var result = await RunAsync(fixture.Root, audit: true);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        CliFixtureAuditResult audit = JsonSerializer.Deserialize<CliFixtureAuditResult>(result.Output, JsonOptions)!;
        Assert.IsTrue(audit.RouteVerified);
        Assert.AreEqual(3, audit.EnumeratedMutations);
        Assert.AreEqual(1, audit.ReadOnlyFileMutations);
        Assert.AreEqual(1, audit.QueuedFileMutations);
        Assert.AreEqual(1, audit.AcknowledgedQueuedFileMutations);
    }

    [TestMethod]
    public async Task AuditAlsoCountsCompletedUploadsForTheRemoteAppliedReadOnlyFile()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await SeedAsync(catalog, fixture.Marker, "cli-roundtrip.txt", MirrorPulseMutationState.Acknowledged);
        var result = await RunAsync(fixture.Root, audit: true);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        CliFixtureAuditResult audit = JsonSerializer.Deserialize<CliFixtureAuditResult>(result.Output, JsonOptions)!;
        Assert.AreEqual(1, audit.ReadOnlyFileMutations);
        Assert.AreEqual(0, audit.QueuedFileMutations);
    }

    [TestMethod]
    public async Task EmptyCatalogIsNotMistakenForMissingRouteEvidence()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var result = await RunAsync(fixture.Root, audit: true);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        CliFixtureAuditResult audit = JsonSerializer.Deserialize<CliFixtureAuditResult>(result.Output, JsonOptions)!;
        Assert.IsTrue(audit.RouteVerified);
        Assert.AreEqual(0, audit.EnumeratedMutations);
        Assert.AreEqual(0, audit.ReadOnlyFileMutations);
    }

    [TestMethod]
    [DataRow("owner-held")]
    [DataRow("missing-marker")]
    [DataRow("missing-catalog")]
    [DataRow("wrong-route")]
    public async Task AuditRejectsLiveOrUnprovenCatalogs(string scenario)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        MirrorPulseProductCatalog? owner = null;
        try
        {
            if (scenario == "owner-held") owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            if (scenario == "missing-marker") File.Delete(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName));
            if (scenario == "missing-catalog") File.Delete(fixture.Paths.ProductCatalogDatabasePath);
            if (scenario == "wrong-route")
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName),
                    JsonSerializer.Serialize(fixture.Marker with { RootKey = "other" }, JsonOptions));
            var result = await RunAsync(fixture.Root, audit: true);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.AreEqual(string.Empty, result.Output.Trim(), "A failed audit cannot emit success evidence.");
            if (scenario == "missing-catalog") Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath));
        }
        finally { if (owner is not null) await owner.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("owner-held")]
    [DataRow("missing-marker")]
    [DataRow("missing-catalog")]
    [DataRow("wrong-route")]
    public async Task CleanupRejectsLiveOrUnprovenFixturesBeforeOpeningCloudFiles(string scenario)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string sentinel = Path.Combine(fixture.Paths.SyncRootPath, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "retain unrelated bytes");
        MirrorPulseProductCatalog? owner = null;
        try
        {
            if (scenario == "owner-held") owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            if (scenario == "missing-marker") File.Delete(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName));
            if (scenario == "missing-catalog") File.Delete(fixture.Paths.ProductCatalogDatabasePath);
            if (scenario == "wrong-route")
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName),
                    JsonSerializer.Serialize(fixture.Marker with { RootKey = "other" }, JsonOptions));
            var result = await RunAsync(fixture.Root, audit: false, cleanup: true);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.AreEqual(string.Empty, result.Output.Trim());
            Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("retain unrelated bytes", await File.ReadAllTextAsync(sentinel));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")), "Rejection must precede Cloud Files startup.");
            if (scenario == "missing-catalog") Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath));
        }
        finally { if (owner is not null) await owner.DisposeAsync(); }
    }

    [TestMethod]
    public async Task CleanupWithoutRecordedProtectionDoesNotOpenCloudFilesOrRewriteOrdinaryPermissions()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string sentinel = Path.Combine(fixture.Paths.SyncRootPath, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "ordinary fixture bytes");
        var before = new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var result = await RunAsync(fixture.Root, audit: false, cleanup: true);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        var cleanup = JsonSerializer.Deserialize<CliFixtureCleanupResult>(result.Output, JsonOptions)!;
        Assert.IsTrue(cleanup.ExclusiveCatalogOwner); Assert.IsFalse(cleanup.ProtectedNamespace);
        Assert.IsFalse(cleanup.CloudRootUnregistered, "An ordinary unprotected fixture must not invoke Cloud Files registration APIs.");
        Assert.AreEqual(0, cleanup.ValidatedObjects); Assert.AreEqual(0, cleanup.RestoredObjects);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
        Assert.AreEqual("ordinary fixture bytes", await File.ReadAllTextAsync(sentinel));
        CollectionAssert.AreEqual(before, new FileInfo(sentinel).GetAccessControl().GetSecurityDescriptorBinaryForm());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalReaderChecksCompleteBytesAndChunkBoundaryRanges(bool corrupt)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        byte[] binary = new byte[CliFixtureProbe.BinaryLength];
        new Random(4096).NextBytes(binary);
        foreach (string directory in new[] { Path.Combine(fixture.Root, "source", "nested"),
            Path.Combine(fixture.Paths.SyncRootPath, fixture.Marker.DirectoryName, "nested") })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.txt"), "0123456789ABCDEF-local-fixture"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.bin"), binary);
        }
        if (corrupt)
        {
            binary[1024 * 1024] ^= 1;
            await File.WriteAllBytesAsync(Path.Combine(fixture.Paths.SyncRootPath, fixture.Marker.DirectoryName, "nested", "fixture.bin"), binary);
        }
        var result = await RunAsync(fixture.Root, audit: false);
        Assert.AreEqual(!corrupt, result.ExitCode == 0, result.Error);
        if (!corrupt)
        {
            CliFixtureReadResult read = JsonSerializer.Deserialize<CliFixtureReadResult>(result.Output, JsonOptions)!;
            Assert.AreEqual(6, read.WholeFileReads);
            Assert.AreEqual(21, read.RangeReads);
            Assert.AreEqual(CliFixtureProbe.BinaryLength, read.BinaryLength);
            Assert.AreEqual(64, read.BinarySha256.Length);
        }
        else
        {
            StringAssert.Contains(result.Error, "hresult=");
            Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public async Task InspectionRefusesAnUnregisteredFixtureWithoutOpeningTheHostCatalog()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        byte[] catalogBefore = await File.ReadAllBytesAsync(fixture.Paths.ProductCatalogDatabasePath);
        var result = await RunAsync(fixture.Root, audit: false, inspect: true);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Output.Trim());
        StringAssert.Contains(result.Error, "hresult=");
        Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(catalogBefore, await File.ReadAllBytesAsync(fixture.Paths.ProductCatalogDatabasePath));
    }

    private static async Task SeedAsync(MirrorPulseProductCatalog catalog, CliFixtureMarker marker, string relative, MirrorPulseMutationState state)
    {
        Guid id = Guid.NewGuid();
        await catalog.PrepareMutationAsync(new(id, InstanceId.Parse(marker.InstanceId), marker.RootKey,
            MirrorPulseWorkerChangeKind.ContentUpdate, relative, null, false, "before", 7, new string('a', 64), MirrorPulseMutationOrigin.Journal));
        if (state == MirrorPulseMutationState.Prepared) return;
        await catalog.TransitionMutationAsync(id, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        if (state == MirrorPulseMutationState.Executing) return;
        if (state is MirrorPulseMutationState.Ambiguous or MirrorPulseMutationState.Conflict)
        {
            await catalog.TransitionMutationAsync(id, MirrorPulseMutationState.Executing, state);
            return;
        }
        await catalog.TransitionMutationAsync(id, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "after");
        if (state == MirrorPulseMutationState.Acknowledged)
            await catalog.TransitionMutationAsync(id, MirrorPulseMutationState.RemoteAccepted, state, "after");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string root, bool audit, bool inspect = false, bool cleanup = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location,
            cleanup ? "--cleanup-cli-fixture" : inspect ? "--inspect-cli-fixture" : audit ? "--audit-cli-fixture" : "--read-cli-fixture", root })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class Fixture(string root, MirrorPulseStoragePaths paths, CliFixtureMarker marker) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public MirrorPulseStoragePaths Paths { get; } = paths;
        public CliFixtureMarker Marker { get; } = marker;

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-cli-integration-" + Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            Directory.CreateDirectory(paths.SyncRootPath);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var manifest = new AdapterManifest(1, AdapterId.Parse("example.cli-fixture"), "CLI fixture", "1.0.0", new(1, 1),
                new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" }, new(null), new(null, null),
                new(true, false, true, true), ["en-US"], "1.0.0", [new("files", "Files", "Files", false)]);
            var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
                new(new string('a', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            await catalog.AddInstallationAsync(installation);
            AdapterInstance instance = await catalog.CreateInstanceAsync(installation.InstallId, "CLI fixture", new Dictionary<string, string>(), [],
                Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"));
            RootRegistration registration = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
            var marker = new CliFixtureMarker(1, instance.InstanceId.ToString(), registration.RootId.ToString(), registration.UniquenessKey, registration.DirectoryName);
            await File.WriteAllTextAsync(Path.Combine(root, CliFixtureProbe.MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
            return new(root, paths, marker);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
