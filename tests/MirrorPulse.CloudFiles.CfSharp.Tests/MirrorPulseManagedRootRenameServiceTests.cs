using System.Runtime.Versioning;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseManagedRootRenameServiceTests
{
    [TestMethod]
    public async Task RecoveryWaitsForItsInstanceWhileAnotherInstanceProgresses()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task gate = scheduler.RunAsync(fixture.Roots[0].InstanceId, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }).AsTask();
        await entered.Task;
        var calls = new List<Guid>();
        var service = new MirrorPulseManagedRootRenameService(fixture.Catalog, scheduler, (operation, _) =>
        {
            calls.Add(operation);
            return ValueTask.FromResult(new MirrorPulseManagedRootRenameRecovery(fixture.Intents.Single(item => item.OperationId == operation), null));
        });
        try
        {
            Task sameInstance = service.RecoverAsync(fixture.Intents[0].OperationId).AsTask();
            await service.RecoverAsync(fixture.Intents[1].OperationId);
            Assert.IsFalse(sameInstance.IsCompleted);
            CollectionAssert.AreEqual(new[] { fixture.Intents[1].OperationId }, calls);
            release.SetResult();
            await sameInstance;
            await gate;
            CollectionAssert.AreEqual(new[] { fixture.Intents[1].OperationId, fixture.Intents[0].OperationId }, calls);
        }
        finally { release.TrySetResult(); await gate; }
    }

    [TestMethod]
    public async Task CancelledWaitingRecoveryRetainsOriginalHistoryWithoutDispatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task gate = scheduler.RunAsync(fixture.Roots[0].InstanceId, async token =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return true;
        }).AsTask();
        await entered.Task;
        int calls = 0;
        var service = new MirrorPulseManagedRootRenameService(fixture.Catalog, scheduler, (_, _) =>
        {
            calls++;
            throw new AssertFailedException("Cancelled recovery must not enter the library boundary.");
        });
        using var cancelled = new CancellationTokenSource();
        try
        {
            Task recovery = service.RecoverAsync(fixture.Intents[0].OperationId, cancelled.Token).AsTask();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => recovery);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(fixture.Intents[0], (await fixture.Catalog.ReadManagedRootRenamesAsync()).Single(item => item.RootId == fixture.Roots[0].RootId));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.RecoverAsync(Guid.NewGuid()).AsTask());
            Assert.AreEqual(0, calls);
        }
        finally { release.TrySetResult(); await gate; }
    }

    [TestMethod]
    public async Task StartupRetainsUnrecoverableFencesAndContinuesOriginalProofs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()), "AQID", DateTimeOffset.UtcNow, "AQID");
        foreach (MirrorPulseRootRenameIntent intent in fixture.Intents.Take(2))
            await fixture.Catalog.SaveManagedRootRenameProofAsync(intent.OperationId, proof);
        var calls = new List<Guid>();
        var service = new MirrorPulseManagedRootRenameService(fixture.Catalog, scheduler, (operation, _) =>
        {
            calls.Add(operation);
            if (operation == fixture.Intents[0].OperationId) throw new IOException("private source and credential must not escape startup");
            return ValueTask.FromResult(new MirrorPulseManagedRootRenameRecovery(fixture.Intents[1], null));
        });
        IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> results = await service.RestorePendingAsync();
        Assert.HasCount(3, results);
        Assert.AreEqual("DirectoryRecoveryUnavailable", results.Single(item => item.OperationId == fixture.Intents[0].OperationId).ErrorCode);
        Assert.IsNotNull(results.Single(item => item.OperationId == fixture.Intents[0].OperationId).FailureHResult);
        Assert.IsNotNull(results.Single(item => item.OperationId == fixture.Intents[1].OperationId).Recovery);
        Assert.AreEqual("OriginalDirectoryProofMissing", results.Single(item => item.OperationId == fixture.Intents[2].OperationId).ErrorCode);
        CollectionAssert.AreEqual(fixture.Intents.Take(2).Select(item => item.OperationId).ToArray(), calls);
        var history = await fixture.Catalog.ReadManagedRootRenameHistoryAsync();
        Assert.IsTrue(history.All(item => item.Intent.Phase == MirrorPulseRootRenamePhase.Prepared));
        Assert.IsTrue(history.Take(2).All(item => item.Proof == proof));
        Assert.IsNull(history[2].Proof);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RestorePendingAsync(cancelled.Token).AsTask());
    }

    [TestMethod]
    public async Task HistoricalReceiptRemainsAddressableAfterAnotherRename()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        MirrorPulseRootRenameIntent old = fixture.Intents[0];
        await fixture.Catalog.TransitionManagedRootRenameAsync(old.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        MirrorPulseRootRenameIntent current = await fixture.Catalog.PrepareManagedRootRenameAsync(old.RootId, "Current Label");
        Guid recovered = Guid.Empty;
        var service = new MirrorPulseManagedRootRenameService(fixture.Catalog, scheduler, (operation, _) =>
        {
            recovered = operation;
            return ValueTask.FromResult(new MirrorPulseManagedRootRenameRecovery(old with { Phase = MirrorPulseRootRenamePhase.Cancelled }, null));
        });
        MirrorPulseManagedRootRenameRecovery receipt = await service.RecoverAsync(old.OperationId);
        Assert.AreEqual(old.OperationId, recovered);
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, receipt.Intent.Phase);
        Assert.AreEqual(current, (await fixture.Catalog.ReadManagedRootRenamesAsync()).Single(item => item.RootId == old.RootId));
    }

    private sealed class Fixture(string directory, MirrorPulseProductCatalog catalog,
        RootRegistration[] roots, MirrorPulseRootRenameIntent[] intents) : IAsyncDisposable
    {
        public MirrorPulseProductCatalog Catalog => catalog;
        public RootRegistration[] Roots => roots;
        public MirrorPulseRootRenameIntent[] Intents => intents;

        public static async Task<Fixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            AdapterId adapter = AdapterId.Parse("example.rename-service");
            var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
                new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                [new("docs", "Docs", "Docs", false), new("media", "Media", "Media", false)]);
            var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            AdapterInstance Instance(string name) => new(adapter, installation.InstallId, InstanceId.New(), name,
                new Dictionary<string, string>(), [], Path.Combine(directory, name, "files"), Path.Combine(directory, name, "transfers"),
                true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            AdapterInstance first = Instance("First"), second = Instance("Second");
            RootRegistration Root(InstanceId instance, string key, string label) => new(adapter, instance, RootId.New(), key, label, label,
                false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
            RootRegistration[] roots = [Root(first.InstanceId, "docs", "FirstDocs"), Root(second.InstanceId, "docs", "SecondDocs"),
                Root(first.InstanceId, "media", "FirstMedia")];
            await catalog.SaveAdapterTopologyAsync(new([installation], [first, second], roots));
            var intents = new List<MirrorPulseRootRenameIntent>();
            foreach (RootRegistration root in roots) intents.Add(await catalog.PrepareManagedRootRenameAsync(root.RootId, root.Label + "Renamed"));
            return new(directory, catalog, roots, intents.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await catalog.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
