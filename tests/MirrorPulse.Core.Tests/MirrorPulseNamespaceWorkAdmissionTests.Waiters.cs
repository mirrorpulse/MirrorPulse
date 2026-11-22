using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceWorkAdmissionTests
{
    [TestMethod]
    public async Task PublicRecoveryWaitsForAnExistingOwnerWhileInternalReentryStillRefuses()
    {
        RootId first = RootId.New(), second = RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first, second]); await OpenAsync(admission, first, second);
        await using var current = await admission.BeginRecoveryAsync([first]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var queued = admission.WaitForRecoveryAsync([first], stop.Token).AsTask();
        try
        {
            Assert.IsFalse(queued.IsCompleted); Assert.IsFalse(admission.IsAdmitted(first));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => admission.BeginRecoveryAsync([first]).AsTask());
            Assert.AreEqual(17, await admission.RunAsync(second, _ => ValueTask.FromResult(17)));
            await current.DisposeAsync();
            await using var owned = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(admission.IsAdmitted(first)); owned.CompleteAfterFreshAudit();
            await owned.DisposeAsync(); Assert.IsTrue(admission.IsAdmitted(first));
        }
        finally { await DisposeRecoveryWaitersAsync(stop, queued); }
    }

    [TestMethod]
    public async Task OverlappingPublicRequestsKeepTheirOrderAndCannotBrieflyReopenStaleReadiness()
    {
        RootId root = RootId.New(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using var current = await admission.BeginRecoveryAsync([root]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = admission.WaitForRecoveryAsync([root], stop.Token).AsTask();
        var second = admission.WaitForRecoveryAsync([root], stop.Token).AsTask();
        try
        {
            await current.DisposeAsync(); await using var one = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(second.IsCompleted); one.CompleteAfterFreshAudit(); await one.DisposeAsync();
            Assert.IsFalse(admission.IsAdmitted(root)); Assert.IsFalse(admission.TryEnter([root], out _));
            await using var two = await second.WaitAsync(TimeSpan.FromSeconds(5));
            two.CompleteAfterFreshAudit(); await two.DisposeAsync(); Assert.IsTrue(admission.IsAdmitted(root));
        }
        finally { await DisposeRecoveryWaitersAsync(stop, first, second); }
    }

    [TestMethod]
    public async Task QueuedRequestDrainsBothSelectedRootsAtomicallyAndLeavesIndependentWorkRunning()
    {
        RootId first = RootId.New(), second = RootId.New(), third = RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first, second, third]); await OpenAsync(admission, first, second, third);
        using var accepted = admission.Enter([second]); await using var current = await admission.BeginRecoveryAsync([first]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var combined = admission.WaitForRecoveryAsync([first, second], stop.Token).AsTask();
        var laterSecond = admission.WaitForRecoveryAsync([second], stop.Token).AsTask();
        try
        {
            Assert.IsFalse(admission.IsAdmitted(second));
            Assert.AreEqual(23, await admission.RunAsync(third, _ => ValueTask.FromResult(23)));
            await current.DisposeAsync(); Assert.IsFalse(combined.IsCompleted); Assert.IsFalse(laterSecond.IsCompleted);
            accepted.Dispose(); await using var both = await combined.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(laterSecond.IsCompleted); await both.DisposeAsync();
            await using var last = await laterSecond.WaitAsync(TimeSpan.FromSeconds(5)); await last.DisposeAsync();
            Assert.IsFalse(admission.IsAdmitted(first)); Assert.IsFalse(admission.IsAdmitted(second)); Assert.IsTrue(admission.IsAdmitted(third));
        }
        finally { accepted.Dispose(); await DisposeRecoveryWaitersAsync(stop, combined, laterSecond); }
    }

    [TestMethod]
    public async Task CancelledWaiterDoesNotOwnOrReleaseTheCurrentScopeAndDoesNotBlockItsSuccessor()
    {
        RootId root = RootId.New(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using var current = await admission.BeginRecoveryAsync([root]);
        using var cancel = new CancellationTokenSource(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var abandoned = admission.WaitForRecoveryAsync([root], cancel.Token).AsTask();
        var successor = admission.WaitForRecoveryAsync([root], stop.Token).AsTask();
        try
        {
            await cancel.CancelAsync(); await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned);
            Assert.IsFalse(successor.IsCompleted);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => admission.BeginRecoveryAsync([root]).AsTask());
            await current.DisposeAsync(); await using var owned = await successor.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(admission.IsAdmitted(root)); await owned.DisposeAsync();
        }
        finally { await DisposeRecoveryWaitersAsync(stop, successor); }
    }

    [TestMethod]
    public async Task PublicRecoveryRequestQueueHasAnExplicitBoundAndCancelledSlotsAreReusable()
    {
        RootId root = RootId.New(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using var current = await admission.BeginRecoveryAsync([root]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Enumerable.Range(0, 64).Select(_ => admission.WaitForRecoveryAsync([root], stop.Token).AsTask()).ToArray();
        try
        {
            Assert.IsTrue(pending.All(task => !task.IsCompleted));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => admission.WaitForRecoveryAsync([root]).AsTask());
            await DisposeRecoveryWaitersAsync(stop, pending);
            using var replacementStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var replacement = admission.WaitForRecoveryAsync([root], replacementStop.Token).AsTask();
            try { await current.DisposeAsync(); await using var owned = await replacement.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await DisposeRecoveryWaitersAsync(replacementStop, replacement); }
        }
        finally { await DisposeRecoveryWaitersAsync(stop, pending); }
    }

    [TestMethod]
    public async Task ShutdownWakesQueuedPublicRecoveryWithoutGrantingAClaimOrReadiness()
    {
        RootId root = RootId.New(); var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using var current = await admission.BeginRecoveryAsync([root]);
        var queued = admission.WaitForRecoveryAsync([root]).AsTask();
        Task stopping = admission.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queued);
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(admission.IsAdmitted(root));
        }
        finally { await current.DisposeAsync(); await stopping.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task DisposeRecoveryWaitersAsync(CancellationTokenSource stop,
        params Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease>[] tasks)
    {
        await stop.CancelAsync();
        foreach (var task in tasks)
        {
            try { await (await task.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
