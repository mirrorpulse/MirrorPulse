using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseNamespaceActorTests
{
    [TestMethod]
    public async Task AllNamespaceCallersShareOneAdmissionWhileAnotherOwnerRemainsFree()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        await using var other = new MirrorPulseNamespaceActor();
        var entered = Signal();
        var release = Signal();
        var secondEntered = Signal();
        Task<int> first = actor.RunAsync(async _ =>
        {
            actor.AssertAccess();
            entered.SetResult();
            await release.Task;
            actor.AssertAccess();
            return 1;
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<int> second = actor.RunAsync(_ =>
            {
                actor.AssertAccess();
                secondEntered.SetResult();
                return ValueTask.FromResult(2);
            }).AsTask();
            Assert.IsFalse(secondEntered.Task.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(3, await other.RunAsync(_ =>
            {
                other.AssertAccess();
                Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
                return ValueTask.FromResult(3);
            }));
            release.SetResult();
            Assert.AreEqual(1, await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(2, await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task CanceledWaiterNeverInvokesItsCallbackOrBlocksFollowingWork()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        var entered = Signal();
        var release = Signal();
        Task<bool> first = actor.RunAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            bool invoked = false;
            Task<bool> queued = actor.RunAsync(_ =>
            {
                invoked = true;
                return ValueTask.FromResult(true);
            }, cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            Assert.IsFalse(invoked);
            release.SetResult();
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(4, await actor.RunAsync(_ => ValueTask.FromResult(4)));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task CancellationAfterEntryDoesNotDetachWorkOrDiscardItsCompletedResult()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        Task<int> work = actor.RunAsync(async token =>
        {
            Assert.AreEqual(cancellation.Token, token);
            entered.SetResult();
            await release.Task;
            Assert.IsTrue(token.IsCancellationRequested);
            actor.AssertAccess();
            return 37;
        }, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.IsFalse(work.IsCompleted);
            Task<int> queued = actor.RunAsync(_ => ValueTask.FromResult(38)).AsTask();
            Assert.IsFalse(queued.IsCompleted);
            release.SetResult();
            Assert.AreEqual(37, await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(38, await queued.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task CallbackFailureReleasesAdmissionAndRestoresTheCallingContext()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        await Assert.ThrowsExactlyAsync<IOException>(() => actor.RunAsync<int>(async _ =>
        {
            actor.AssertAccess();
            await Task.Yield();
            throw new IOException("fixture callback failure");
        }).AsTask());
        Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
        Assert.AreEqual(5, await actor.RunAsync(_ => ValueTask.FromResult(5)));
    }

    [TestMethod]
    public async Task NestedEntryAndDisposalAreRejectedAcrossAwaitAndChildTasks()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        Assert.IsTrue(await actor.RunAsync(async _ =>
        {
            await Task.Yield();
            actor.AssertAccess();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                actor.RunAsync(_ => ValueTask.FromResult(false)).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => actor.DisposeAsync().AsTask());
            await Task.Run(async () =>
            {
                actor.AssertAccess();
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    actor.RunAsync(_ => ValueTask.FromResult(false)).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => actor.DisposeAsync().AsTask());
            }, CancellationToken.None);
            return true;
        }));
        Assert.IsTrue(await actor.RunAsync(_ => ValueTask.FromResult(true)));
    }

    [TestMethod]
    public async Task CapturedExecutionContextCannotReuseACompletedAdmission()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        var releaseChild = Signal();
        Task? child = null;
        try
        {
            Assert.IsTrue(await actor.RunAsync(_ =>
            {
                actor.AssertAccess();
                child = Task.Run(async () =>
                {
                    await releaseChild.Task;
                    Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
                }, CancellationToken.None);
                return ValueTask.FromResult(true);
            }));
            releaseChild.SetResult();
            await child!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
        }
        finally { releaseChild.TrySetResult(); }
    }

    [TestMethod]
    public async Task ShutdownCancelsWaitersAndEveryDisposerDrainsTheActiveCallback()
    {
        var actor = new MirrorPulseNamespaceActor();
        var entered = Signal();
        var release = Signal();
        bool queuedInvoked = false;
        Task<int> active = actor.RunAsync(async token =>
        {
            entered.SetResult();
            await release.Task;
            Assert.IsFalse(token.IsCancellationRequested);
            actor.AssertAccess();
            return 41;
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<bool> queued = actor.RunAsync(_ =>
            {
                queuedInvoked = true;
                return ValueTask.FromResult(true);
            }).AsTask();
            Task firstDisposal = actor.DisposeAsync().AsTask();
            Task secondDisposal = actor.DisposeAsync().AsTask();
            Assert.AreSame(firstDisposal, secondDisposal);
            Assert.IsFalse(firstDisposal.IsCompleted);
            Assert.IsFalse(secondDisposal.IsCompleted);
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            Assert.IsFalse(queuedInvoked);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                actor.RunAsync(_ => ValueTask.FromResult(false)).AsTask());
            release.SetResult();
            Assert.AreEqual(41, await active.WaitAsync(TimeSpan.FromSeconds(10)));
            await firstDisposal.WaitAsync(TimeSpan.FromSeconds(10));
            await secondDisposal.WaitAsync(TimeSpan.FromSeconds(10));
            await actor.DisposeAsync();
            Assert.ThrowsExactly<InvalidOperationException>(actor.AssertAccess);
        }
        finally
        {
            release.TrySetResult();
            await actor.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AlreadyCanceledInputNeverReceivesAdmission()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool invoked = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => actor.RunAsync(_ =>
        {
            invoked = true;
            return ValueTask.FromResult(true);
        }, cancellation.Token).AsTask());
        Assert.IsFalse(invoked);
        Assert.IsTrue(await actor.RunAsync(_ => ValueTask.FromResult(true)));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
