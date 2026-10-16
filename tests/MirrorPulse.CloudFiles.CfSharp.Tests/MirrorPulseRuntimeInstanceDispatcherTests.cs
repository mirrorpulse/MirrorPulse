using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRuntimeInstanceDispatcherTests
{
    [TestMethod]
    public async Task QueuedControlledWorkDoesNotOwnTheLibraryNeededByTheEarlierInstanceOperation()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler();
        using var library = new SemaphoreSlim(1, 1);
        var instance = InstanceId.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async ValueTask<int> WithLibrary(Func<CancellationToken, ValueTask<int>> work, CancellationToken token)
        {
            await library.WaitAsync(token);
            try { return await work(token); }
            finally { library.Release(); }
        }
        var earlier = scheduler.RunAsync(instance, async token =>
        {
            entered.SetResult(); await proceed.Task.WaitAsync(token);
            return await WithLibrary(_ => ValueTask.FromResult(1), token);
        }, deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var controlled = MirrorPulseRuntimeInstanceDispatcher.RunAsync(scheduler, instance, WithLibrary,
            _ => ValueTask.FromResult(2), deadline.Token).AsTask();
        // The earlier operation already owns the actual scheduler; the old dispatcher
        // synchronously reserves the library before waiting for that instance to drain.
        proceed.SetResult();
        try
        {
            Assert.AreEqual(1, await earlier.WaitAsync(deadline.Token));
            Assert.AreEqual(2, await controlled.WaitAsync(deadline.Token));
        }
        finally
        {
            deadline.Cancel(); proceed.TrySetResult();
            try { await Task.WhenAll(earlier, controlled); } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task CancellationWhileQueuedNeverAcquiresTheLibraryAndAnotherInstanceCanUseIt()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New();
        await using var held = await scheduler.AcquireAsync([instance]);
        using var cancelled = new CancellationTokenSource(); int libraryCalls = 0;
        ValueTask<int> WithLibrary(Func<CancellationToken, ValueTask<int>> work, CancellationToken token)
        { libraryCalls++; return work(token); }
        var queued = MirrorPulseRuntimeInstanceDispatcher.RunAsync(scheduler, instance, WithLibrary,
            _ => ValueTask.FromResult(1), cancelled.Token).AsTask();
        Assert.IsFalse(queued.IsCompleted); Assert.AreEqual(0, libraryCalls);
        Assert.AreEqual(2, await MirrorPulseRuntimeInstanceDispatcher.RunAsync(scheduler, InstanceId.New(), WithLibrary,
            _ => ValueTask.FromResult(2), default));
        cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.AreEqual(1, libraryCalls);
    }

    [TestMethod]
    public async Task QueuedOperationBindsItsCurrentLibraryOnlyAfterInstanceAdmission()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New();
        await using var held = await scheduler.AcquireAsync([instance]);
        int currentOwner = 1; int boundOwner = 0;
        ValueTask<int> WithLibrary(Func<CancellationToken, ValueTask<int>> work, CancellationToken token)
        { boundOwner = currentOwner; return work(token); }
        var queued = MirrorPulseRuntimeInstanceDispatcher.RunAsync(scheduler, instance, WithLibrary,
            _ => ValueTask.FromResult(boundOwner), default).AsTask();
        Assert.AreEqual(0, boundOwner); currentOwner = 2;
        await held.DisposeAsync(); Assert.AreEqual(2, await queued.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
