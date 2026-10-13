using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseFullRescanRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedDiscoveryKeepsGenerationAndDeferredRootsAcrossRestart(bool cancelled)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        RootId offline = new(Guid.NewGuid());
        Guid generation;
        int acknowledgements = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.RequireRootRescanAsync(offline);
                var recovery = new MirrorPulseFullRescanRecovery(catalog,
                    _ => cancelled ? throw new OperationCanceledException() : throw new UnauthorizedAccessException(),
                    _ => { acknowledgements++; return ValueTask.CompletedTask; }, (_, _) => ValueTask.CompletedTask);
                if (cancelled) await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => recovery.RunAsync(new(true)).AsTask());
                else await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => recovery.RunAsync(new(true)).AsTask());
                MirrorPulseFullRescanCheckpoint checkpoint = (await catalog.ReadFullRescanAsync())!;
                generation = checkpoint.Generation;
                Assert.AreEqual(MirrorPulseFullRescanPhase.Running, checkpoint.Phase);
                Assert.AreEqual(0, acknowledgements);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.AreEqual(generation, (await reopened.BeginFullRescanAsync()).Generation);
            CollectionAssert.AreEqual(new[] { offline }, (await reopened.ReadDeferredRescanRootsAsync()).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.CompleteFullRescanAsync(Guid.NewGuid()));
            var resumed = new MirrorPulseFullRescanRecovery(reopened, _ => ValueTask.FromResult(1),
                _ => { acknowledgements++; return ValueTask.CompletedTask; }, (_, _) => ValueTask.CompletedTask);
            Assert.IsTrue((await resumed.RunAsync(new(false))).WasRequired);
            Assert.AreEqual(1, acknowledgements);
            Assert.IsNull(await reopened.ReadFullRescanAsync());
            // Completing the online scan cannot discard the offline root's obligation.
            CollectionAssert.AreEqual(new[] { offline }, (await reopened.ReadDeferredRescanRootsAsync()).ToArray());
            MirrorPulseRootRescanRequest request = (await reopened.ReadRootRescanRequestsAsync()).Single();
            Assert.IsTrue(await reopened.CompleteRootRescanAsync(offline, request.Generation));
            Assert.IsEmpty(await reopened.ReadDeferredRescanRootsAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AckProjectionGapReplaysDiscoveryWithoutRepeatingRemoteWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent() with { Origin = MirrorPulseMutationOrigin.Rescan };
        int mutations = 0;
        int discoveries = 0;
        int acknowledgements = 0;
        try
        {
            async ValueTask<int> Scan(MirrorPulseProductCatalog catalog, CancellationToken token)
            {
                discoveries++;
                await new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent, async mutationToken =>
                {
                    mutations++;
                    await File.WriteAllTextAsync(Path.Combine(root, "remote"), "accepted", mutationToken);
                    return "accepted";
                }, (_, _) => ValueTask.CompletedTask, token);
                return 1;
            }
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var recovery = new MirrorPulseFullRescanRecovery(catalog, token => Scan(catalog, token),
                    _ => { acknowledgements++; return ValueTask.CompletedTask; }, (_, _) => throw new IOException("Projection unavailable."));
                await Assert.ThrowsExactlyAsync<IOException>(() => recovery.RunAsync(new(true)).AsTask());
                Assert.AreEqual(MirrorPulseFullRescanPhase.Acknowledging, (await catalog.ReadFullRescanAsync())!.Phase);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var resumed = new MirrorPulseFullRescanRecovery(reopened, token => Scan(reopened, token),
                _ => { acknowledgements++; return ValueTask.CompletedTask; }, (_, _) => ValueTask.CompletedTask);
            await resumed.RunAsync(new(false));
            Assert.AreEqual(2, discoveries);
            Assert.AreEqual(2, acknowledgements);
            Assert.AreEqual(1, mutations);
            Assert.AreEqual("accepted", await File.ReadAllTextAsync(Path.Combine(root, "remote")));
            Assert.IsNull(await reopened.ReadFullRescanAsync());
        }
        finally { Directory.Delete(root, true); }
    }
}
