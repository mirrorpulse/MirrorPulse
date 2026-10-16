using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRuntimeRecoveryDispatcherTests
{
    [TestMethod]
    public async Task EarlierRootWorkCanUseTheLibraryAndInstanceBeforeRecoveryRebindsThem()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var instance = InstanceId.New(); var root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await using (var initial = await work.BeginRecoveryAsync([root])) initial.CompleteAfterFreshAudit();
        using var library = new SemaphoreSlim(1, 1); using var requests = new SemaphoreSlim(1, 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var earlierEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int owner = 1;
        async ValueTask<T> WithLibrary<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken token)
        {
            await library.WaitAsync(token);
            try { return await action(token); }
            finally { library.Release(); }
        }
        var accepted = work.Enter([root]);
        var earlier = scheduler.RunAsync(instance, async token =>
        {
            try
            {
                earlierEntered.SetResult(); await proceed.Task.WaitAsync(token);
                return await WithLibrary(_ => ValueTask.FromResult(owner), token);
            }
            finally { accepted.Dispose(); }
        }, deadline.Token).AsTask();
        await earlierEntered.Task.WaitAsync(deadline.Token);
        async ValueTask<(int Preparation, Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> Drain)> Prepare(CancellationToken token) =>
            await WithLibrary(_ =>
            {
                var drain = DrainAsync(work.BeginRecoveryAsync([root], token));
                preflight.SetResult(); return ValueTask.FromResult((owner, drain));
            }, token);
        var recovery = MirrorPulseRuntimeInstanceDispatcher.RunRecoveryAsync(requests, scheduler, [instance], Prepare,
            (original, admission, instances, token) => WithLibrary(async stop =>
            {
                Assert.AreEqual(1, original); Assert.IsFalse(work.IsAdmitted(root));
                owner = 2;
                var observed = await instances.RunAsync(instance, _ => ValueTask.FromResult(owner), stop);
                admission!.CompleteAfterFreshAudit(); return observed;
            }, token), deadline.Token).AsTask();
        try
        {
            await preflight.Task.WaitAsync(deadline.Token); Assert.IsFalse(work.IsAdmitted(root));
            Assert.IsFalse(recovery.IsCompleted); proceed.SetResult();
            Assert.AreEqual(1, await earlier.WaitAsync(deadline.Token));
            Assert.AreEqual(2, await recovery.WaitAsync(deadline.Token)); Assert.IsTrue(work.IsAdmitted(root));
        }
        finally
        {
            proceed.TrySetResult(); deadline.Cancel(); accepted.Dispose();
            try { await Task.WhenAll(earlier, recovery); } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task CancelledLibraryPreflightDoesNotCloseAQueuedRecoveryRoot()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await using (var initial = await work.BeginRecoveryAsync([root])) initial.CompleteAfterFreshAudit();
        using var library = new SemaphoreSlim(0, 1); using var requests = new SemaphoreSlim(1, 1);
        using var cancelled = new CancellationTokenSource(); bool executed = false;
        async ValueTask<(int Preparation, Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> Drain)> Prepare(CancellationToken token)
        {
            await library.WaitAsync(token);
            try { return (1, DrainAsync(work.BeginRecoveryAsync([root], token))); }
            finally { library.Release(); }
        }
        var recovery = MirrorPulseRuntimeInstanceDispatcher.RunRecoveryAsync(requests, scheduler, [InstanceId.New()], Prepare,
            (_, _, _, _) => { executed = true; return ValueTask.FromResult(true); }, cancelled.Token).AsTask();
        Assert.IsFalse(recovery.IsCompleted); Assert.IsTrue(work.IsAdmitted(root));
        cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => recovery);
        Assert.IsTrue(work.IsAdmitted(root)); Assert.IsFalse(executed); Assert.AreEqual(1, requests.CurrentCount);
    }

    [TestMethod]
    public async Task CancellationDuringRootDrainLeavesItClosedAndReleasesTheRecoveryOwner()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var root = RootId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await using (var initial = await work.BeginRecoveryAsync([root])) initial.CompleteAfterFreshAudit();
        using var requests = new SemaphoreSlim(1, 1); using var cancelled = new CancellationTokenSource();
        using var earlier = work.Enter([root]); bool executed = false;
        ValueTask<(int Preparation, Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> Drain)> Prepare(CancellationToken token) =>
            ValueTask.FromResult((1, DrainAsync(work.BeginRecoveryAsync([root], token))));
        var recovery = MirrorPulseRuntimeInstanceDispatcher.RunRecoveryAsync(requests, scheduler, [InstanceId.New()], Prepare,
            (_, _, _, _) => { executed = true; return ValueTask.FromResult(true); }, cancelled.Token).AsTask();
        Assert.IsFalse(recovery.IsCompleted); Assert.IsFalse(work.IsAdmitted(root));
        cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => recovery); Assert.IsFalse(executed);
        Assert.IsFalse(work.IsAdmitted(root)); earlier.Dispose();
        await using var retry = await work.BeginRecoveryAsync([root]); retry.CompleteAfterFreshAudit();
    }

    [TestMethod]
    public async Task CancelledInstanceAdmissionReleasesDrainedRootsWithoutRestoringReadiness()
    {
        await using var scheduler = new MirrorPulseInstanceScheduler(); var root = RootId.New(); var instance = InstanceId.New();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root]);
        await using var occupied = await scheduler.AcquireAsync([instance]);
        using var requests = new SemaphoreSlim(1, 1); using var cancelled = new CancellationTokenSource(); bool executed = false;
        ValueTask<(int Preparation, Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> Drain)> Prepare(CancellationToken token) =>
            ValueTask.FromResult((1, DrainAsync(work.BeginRecoveryAsync([root], token))));
        var recovery = MirrorPulseRuntimeInstanceDispatcher.RunRecoveryAsync(requests, scheduler, [instance], Prepare,
            (_, _, _, _) => { executed = true; return ValueTask.FromResult(true); }, cancelled.Token).AsTask();
        Assert.IsFalse(recovery.IsCompleted); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => recovery);
        Assert.IsFalse(executed); Assert.IsFalse(work.IsAdmitted(root));
        await using var retry = await work.BeginRecoveryAsync([root]); retry.CompleteAfterFreshAudit();
    }

    private static async Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease?> DrainAsync(
        ValueTask<MirrorPulseNamespaceWorkAdmission.RecoveryLease> pending) => await pending;
}
