using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task DisconnectedDeploymentWaitsAndReconnectsWhileAnotherDeploymentIsStillBlocked()
    {
        var blocked = AdmissionRoot("Slow");
        var offline = AdmissionRoot("Offline");
        var source = new BackgroundPollSource(blocked.InstanceId);
        await using var poller = new MirrorPulseActiveRemotePoller(source, [AdmissionAdapter(blocked), AdmissionAdapter(offline)],
            [blocked, offline], (_, _, _) => throw new InvalidOperationException("Empty sources cannot generate a batch."),
            interval: TimeSpan.FromMinutes(1));

        await poller.StartAsync();
        await source.BlockedRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.FailedRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(await poller.RefreshAsync(offline.InstanceId, force: false));
        Assert.AreEqual(1, source.OfflineReads);
        var retry = poller.ReadRootStatus(offline);
        Assert.AreEqual("Retrying", retry.Phase);
        Assert.AreEqual("RootOffline", retry.Reason);
        Assert.IsTrue(retry.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(30));

        source.AllowRemoteRead.TrySetResult();
        poller.NotifyWorkerConnected(offline.InstanceId);
        await source.ReconnectedRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (poller.ReadRootStatus(offline).Phase != "Current") await Task.Delay(10, timeout.Token);
        var recovered = poller.ReadRootStatus(offline);
        Assert.IsNull(recovered.Reason);
        Assert.IsNull(recovered.NextAttemptAt);
        Assert.IsNotNull(recovered.LastSuccessfulScan);
        Assert.AreEqual(2, source.OfflineReads);
        Assert.AreEqual("Waiting", poller.ReadRootStatus(blocked).Phase);
    }

    private sealed class BackgroundPollSource(InstanceId slow) : IMirrorPulseDirectoryPageSource
    {
        internal readonly TaskCompletionSource BlockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FailedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReconnectedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowRemoteRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offlineReads;
        internal int OfflineReads => Volatile.Read(ref _offlineReads);

        public async ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken)
        {
            if (instanceId == slow)
            {
                BlockedRead.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Interlocked.Increment(ref _offlineReads);
            if (!AllowRemoteRead.Task.IsCompleted)
            {
                FailedRead.TrySetResult();
                throw new AdapterWorkerOperationException("RootOffline");
            }

            ReconnectedRead.TrySetResult();
            return new([], ReadOnlyMemory<byte>.Empty, true);
        }
    }
}
