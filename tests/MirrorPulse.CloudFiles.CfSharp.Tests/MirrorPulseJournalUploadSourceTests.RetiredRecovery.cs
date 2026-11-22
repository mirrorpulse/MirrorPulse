using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
public sealed partial class MirrorPulseJournalUploadSourceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeEmptyJournalCannotSuspendAnUnfinishedRetiredRecoveryRootScan()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var cloud = new CfSharpMirrorPulseCloudRootRegistry();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            cloud.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
            await fileSystem.StartAsync(timeout.Token);
            await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 4 });
            await feed.StartAsync(timeout.Token);
            var roots = Enumerable.Range(0, 13).Select(index => AdapterRootRegistrationMapper.Map(
                AdapterId.Parse("example.recovery"), InstanceId.New(),
                new AdapterRootDefinition("local", "Root " + index, "Root " + index, false),
                RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot)).ToArray();
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            var recovery = new RecoveryRootProbe(router);
            var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true, retiredSources: recovery);
            for (int turn = 0; turn < 3; turn++)
            {
                var batch = await source.ReadPendingAsync(timeout.Token);
                Assert.IsEmpty(batch.ReadyCommands); Assert.IsFalse(batch.RequiresFullRescan);
                Assert.IsTrue(recovery.RecoveryScanInProgress);
                Assert.HasCount((turn + 1) * 4, recovery.Visited);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            wait.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.ReadPendingAsync(wait.Token));
            Assert.IsFalse(recovery.RecoveryScanInProgress);
            Assert.HasCount(13, recovery.Visited);
            await using var read = await state.OpenStore.BeginTransactionAsync(timeout.Token);
            Assert.IsEmpty(await read.Operations.ListAsync(64, timeout.Token));
            await read.RollbackAsync(timeout.Token);
            TestContext.WriteLine("RetiredRecoveryRootScan: roots=13; maximumInspectedPerTurn=4; emptyJournalContinuesIncompleteRecovery=True; completeScanResumesFeedWait=True; originalAcknowledgements=0; remoteCalls=0.");
        }
        finally
        {
            cloud.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RecoveryRootProbe(MirrorPulseRootRouter router) : IMirrorPulseRetiredTransferObservationHandler
    {
        private readonly MirrorPulseRetiredTransferRootCursor _cursor = new();
        internal HashSet<RootId> Visited { get; } = [];
        public bool RecoveryScanInProgress => _cursor.RecoveryInProgress;
        public ValueTask RecoverPendingAsync(CancellationToken token)
        {
            int inspected = 0;
            var window = _cursor.Read(router, _ => { inspected++; return true; }, token);
            Assert.IsLessThanOrEqualTo(4, inspected);
            foreach (var root in window.Roots) Assert.IsTrue(Visited.Add(root.RootId));
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> TryAcknowledgeAsync(MirrorPulseLocalChangeObservation observation,
            MirrorPulseRetiredTransferSource source, CancellationToken token) =>
            throw new AssertFailedException("An empty official journal has no original notification to acknowledge.");
    }
}
