using System.Diagnostics;
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

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string root, bool audit)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, audit ? "--audit-cli-fixture" : "--read-cli-fixture", root })
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
