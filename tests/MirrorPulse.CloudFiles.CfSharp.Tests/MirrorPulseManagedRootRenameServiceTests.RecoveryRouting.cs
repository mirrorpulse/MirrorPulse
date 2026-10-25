using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseManagedRootRenameServiceTests
{
    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("missing-proof")]
    [DataRow("missing-id")]
    public async Task RootRecoveryRouterPreservesLegacyReceiptsAndRefusesMissingOriginalsWithoutStartingTheSdk(string scenario)
    {
        await using var fixture = await CreateRecoveryRoutingFixtureAsync();
        var intent = fixture.Intents[0];
        if (scenario == "cancelled")
            await fixture.Catalog.TransitionManagedRootRenameAsync(intent.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        var histories = await fixture.Catalog.ReadManagedRootRenameHistoryAsync();
        var topology = await fixture.Catalog.ReadAdapterTopologyAsync();
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
        await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(state)
            .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, fixture.Roots);
        var recovery = new MirrorPulseManagedRootRenameRecoveryRouter(fileSystem, state, fixture.Catalog, router, null, null);
        if (scenario == "cancelled")
        {
            var result = await recovery.RecoverAsync(intent.OperationId, CancellationToken.None);
            Assert.AreEqual(histories.Single(history => history.Intent.OperationId == intent.OperationId).Intent, result.Intent);
            Assert.IsNull(result.LibraryResult);
        }
        else if (scenario == "missing-proof")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => recovery.RecoverAsync(intent.OperationId, CancellationToken.None).AsTask());
        else
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => recovery.RecoverAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
        CollectionAssert.AreEqual(histories.ToArray(), (await fixture.Catalog.ReadManagedRootRenameHistoryAsync()).ToArray());
        CollectionAssert.AreEqual(topology.Roots.ToArray(), (await fixture.Catalog.ReadAdapterTopologyAsync()).Roots.ToArray());
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath).Any());
    }

    private static async Task<Fixture> CreateRecoveryRoutingFixtureAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        var adapter = AdapterId.Parse("example.original-root-recovery-routing");
        var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(adapter, installation.InstallId, InstanceId.New(), "First",
            new Dictionary<string, string>(), [], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
            true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var root = new RootRegistration(adapter, instance.InstanceId, RootId.New(), "docs", "Docs", "Docs",
            false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
        await catalog.SaveAdapterTopologyAsync(new([installation], [instance], [root]));
        var intent = await catalog.PrepareManagedRootRenameAsync(root.RootId, "Renamed");
        return new(directory, catalog, [root], [intent]);
    }
}
