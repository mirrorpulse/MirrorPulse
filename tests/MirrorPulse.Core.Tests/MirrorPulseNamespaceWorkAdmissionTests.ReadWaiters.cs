using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceWorkAdmissionTests
{
    [TestMethod]
    public async Task WaitingReadCannotOvertakeQueuedRecoveryAndRetainsItsLeaseAfterReadmission()
    {
        RootId root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(work, root);
        await using var first = await work.BeginRecoveryAsync([root]);
        var read = work.EnterAfterRecoveryAsync([root], CancellationToken.None).AsTask();
        var queued = work.WaitForRecoveryAsync([root]).AsTask();
        Assert.IsFalse(read.IsCompleted);
        first.CompleteAfterFreshAudit(); await first.DisposeAsync();
        await using var second = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(read.IsCompleted);
        second.CompleteAfterFreshAudit(); await second.DisposeAsync();
        using var lease = await read.WaitAsync(TimeSpan.FromSeconds(5));
        var draining = work.BeginRecoveryAsync([root]).AsTask();
        Assert.IsFalse(draining.IsCompleted);
        lease.Dispose();
        await using var final = await draining.WaitAsync(TimeSpan.FromSeconds(5));
        final.CompleteAfterFreshAudit();
    }

    [TestMethod]
    public async Task CancelledReadDoesNotDelayRecoveryOrReuseItsAudit()
    {
        RootId root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(work, root);
        await using var recovery = await work.BeginRecoveryAsync([root]);
        using var stop = new CancellationTokenSource();
        var read = work.EnterAfterRecoveryAsync([root], stop.Token).AsTask();
        Assert.IsFalse(read.IsCompleted); stop.Cancel();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(work.IsAdmitted(root));
        recovery.CompleteAfterFreshAudit(); await recovery.DisposeAsync();
        using var next = await work.EnterAfterRecoveryAsync([root], CancellationToken.None);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("closed")]
    [DataRow("failed")]
    public async Task WaitingReadRefusesUnknownOrUnauditedRoots(string state)
    {
        RootId root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission(state == "unknown" ? [] : [root]);
        Task<MirrorPulseNamespaceWorkAdmission.WorkLease> read;
        if (state == "failed")
        {
            await OpenAsync(work, root);
            await using var recovery = await work.BeginRecoveryAsync([root]);
            read = work.EnterAfterRecoveryAsync([root], CancellationToken.None).AsTask();
            Assert.IsFalse(read.IsCompleted);
            await recovery.DisposeAsync();
        }
        else read = work.EnterAfterRecoveryAsync([root], CancellationToken.None).AsTask();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(work.IsAdmitted(root));
    }

    [TestMethod]
    public async Task DisposalWakesWaitingReadsAndStillDrainsOriginalRecovery()
    {
        RootId root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(work, root);
        await using var recovery = await work.BeginRecoveryAsync([root]);
        var read = work.EnterAfterRecoveryAsync([root], CancellationToken.None).AsTask();
        var disposing = work.DisposeAsync().AsTask();
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(disposing.IsCompleted);
        recovery.CompleteAfterFreshAudit(); await recovery.DisposeAsync();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(work.IsAdmitted(root));
    }

    [TestMethod]
    public async Task PendingReadCapacityIsBoundedAndCancellationReleasesEverySlot()
    {
        RootId root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(work, root);
        await using var recovery = await work.BeginRecoveryAsync([root]);
        using var stop = new CancellationTokenSource();
        var reads = Enumerable.Range(0, 256).Select(_ => work.EnterAfterRecoveryAsync([root], stop.Token).AsTask()).ToArray();
        Assert.IsTrue(reads.All(read => !read.IsCompleted));
        Assert.AreEqual(256, work.PendingReadCount);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => work.EnterAfterRecoveryAsync([root], stop.Token).AsTask());
        stop.Cancel();
        foreach (var read in reads)
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, work.PendingReadCount);
        recovery.CompleteAfterFreshAudit(); await recovery.DisposeAsync();
        using var next = await work.EnterAfterRecoveryAsync([root], CancellationToken.None);
    }

    [TestMethod]
    public async Task MixedRootReadWaitNeverPartiallyAdmitsAnotherRoot()
    {
        RootId first = RootId.New(), second = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([first, second]);
        await OpenAsync(work, first, second);
        await using var busy = await work.BeginRecoveryAsync([first]);
        var read = work.EnterAfterRecoveryAsync([first, second], CancellationToken.None).AsTask();
        var independent = work.BeginRecoveryAsync([second]).AsTask();
        Assert.IsTrue(independent.IsCompletedSuccessfully);
        await using (var recovery = await independent) recovery.CompleteAfterFreshAudit();
        Assert.IsFalse(read.IsCompleted);
        busy.CompleteAfterFreshAudit(); await busy.DisposeAsync();
        using var both = await read.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
