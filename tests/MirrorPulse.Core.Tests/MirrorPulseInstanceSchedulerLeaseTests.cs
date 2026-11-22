using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseInstanceSchedulerLeaseTests
{
    [TestMethod]
    public async Task RetainedInstancesSerializeTheirLanesAndDoNotAdmitAnUnrelatedInstance()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var first = InstanceId.New(); var second = InstanceId.New();
        await using var lease = await scheduler.AcquireAsync([first, second]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var active = lease.RunAsync(first, async token => { entered.SetResult(); await release.Task.WaitAsync(token); return 1; }, deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var queued = lease.RunAsync(first, _ => ValueTask.FromResult(2), deadline.Token).AsTask();
        var external = scheduler.RunAsync(first, _ => ValueTask.FromResult(3), deadline.Token).AsTask();
        try
        {
            Assert.IsFalse(queued.IsCompleted); Assert.IsFalse(external.IsCompleted);
            Assert.AreEqual(4, await lease.RunAsync(second, _ => ValueTask.FromResult(4), deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lease.RunAsync(InstanceId.New(), _ => ValueTask.FromResult(0)).AsTask());
            release.SetResult(); Assert.AreEqual(1, await active); Assert.AreEqual(2, await queued);
            Assert.IsFalse(external.IsCompleted); await lease.DisposeAsync(); Assert.AreEqual(3, await external);
        }
        finally { release.TrySetResult(); deadline.Cancel(); try { await Task.WhenAll(active, queued, external); } catch (OperationCanceledException) { } }
    }

    [TestMethod]
    public async Task CancellingPartialGroupAcquisitionReleasesOnlyTheGatesItObtained()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var instances = new[] { InstanceId.New(), InstanceId.New() }.OrderBy(instance => instance.Value).ToArray();
        await using var occupied = await scheduler.AcquireAsync([instances[1]]);
        using var cancelled = new CancellationTokenSource();
        var acquiring = scheduler.AcquireAsync(instances.Reverse(), cancelled.Token).AsTask();
        var first = scheduler.RunAsync(instances[0], _ => ValueTask.FromResult(true), deadline.Token).AsTask();
        Assert.IsFalse(acquiring.IsCompleted); Assert.IsFalse(first.IsCompleted);
        cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => acquiring);
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(await occupied.RunAsync(instances[1], _ => ValueTask.FromResult(true)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scheduler.AcquireAsync([instances[0], instances[0]]).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scheduler.AcquireAsync(Enumerable.Range(0, 4097).Select(_ => InstanceId.New())).AsTask());
    }

    [TestMethod]
    public async Task ThrowingCancellationCallbackStillDrainsTheAdmittedTailAndReleasesItsInstance()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New();
        var lease = await scheduler.AcquireAsync([instance]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var active = lease.RunAsync(instance, async token =>
        {
            using var registration = token.Register(() => { cancelled.TrySetResult(); throw new InvalidOperationException("Synthetic cancellation callback failure."); });
            entered.SetResult(); await release.Task.WaitAsync(deadline.Token); return true;
        }).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var closing = lease.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(deadline.Token); release.SetResult();
            Assert.IsTrue(await active.WaitAsync(deadline.Token));
            await Assert.ThrowsAsync<AggregateException>(() => closing);
            Assert.IsTrue(await scheduler.RunAsync(instance, _ => ValueTask.FromResult(true), deadline.Token));
        }
        finally { release.TrySetResult(); await active; try { await closing; } catch (AggregateException) { } }
    }

    [TestMethod]
    public async Task SchedulerShutdownWithAThrowingCallbackStillWaitsForItsAdmittedTail()
    {
        var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New();
        var lease = await scheduler.AcquireAsync([instance]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var active = lease.RunAsync(instance, async token =>
        {
            using var registration = token.Register(() => { cancelled.TrySetResult(); throw new InvalidOperationException("Synthetic cancellation callback failure."); });
            entered.SetResult(); await release.Task.WaitAsync(deadline.Token); return true;
        }).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var closing = scheduler.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(deadline.Token);
            // Observe the cancellation failure without releasing the accepted operation.
            // Both callers must still wait for that operation and its physical lease.
            var repeated = scheduler.DisposeAsync().AsTask();
            Assert.IsFalse(repeated.IsCompleted, "Repeated shutdown must retain the original drain even when a callback throws.");
            release.SetResult(); Assert.IsTrue(await active.WaitAsync(deadline.Token));
            await lease.DisposeAsync();
            await Assert.ThrowsAsync<AggregateException>(() => closing);
            await Assert.ThrowsAsync<AggregateException>(() => repeated);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.RunAsync(instance, _ => ValueTask.FromResult(false)).AsTask());
        }
        finally
        {
            release.TrySetResult(); await active; await lease.DisposeAsync();
            try { await closing; } catch (AggregateException) { }
        }
    }

    [TestMethod]
    public async Task SchedulerShutdownCancelsWaitersButDrainsAnAlreadyAdmittedNonCooperativeTail()
    {
        var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New();
        var lease = await scheduler.AcquireAsync([instance]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = lease.RunAsync(instance, async _ => { entered.SetResult(); await release.Task; return true; }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = scheduler.RunAsync(instance, _ => ValueTask.FromResult(false)).AsTask();
        var closing = scheduler.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            var releasing = lease.DisposeAsync().AsTask(); Assert.IsFalse(releasing.IsCompleted); Assert.IsFalse(closing.IsCompleted);
            release.SetResult(); Assert.IsTrue(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            await releasing.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await lease.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.RunAsync(instance, _ => ValueTask.FromResult(false)).AsTask());
        }
        finally { release.TrySetResult(); await active; await lease.DisposeAsync(); await scheduler.DisposeAsync(); }
    }
}
