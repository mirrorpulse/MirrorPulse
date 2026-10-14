using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseNamespaceWorkAdmissionTests
{
    [TestMethod]
    public async Task RecoveryClosesBeforeDrainAndLeavesAnotherRootWorking()
    {
        RootId first = RootId.New(), second = RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first, second]);
        Assert.IsFalse(admission.IsAdmitted(first));
        await OpenAsync(admission, first, second);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = admission.RunAsync(first, async _ => { entered.SetResult(); await release.Task; return 17; }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MirrorPulseNamespaceWorkAdmission.RecoveryLease> recovery = admission.BeginRecoveryAsync([first]).AsTask();
        Assert.IsFalse(recovery.IsCompleted); Assert.IsFalse(admission.IsAdmitted(first));
        int blockedCalls = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await admission.RunAsync(first, _ => { blockedCalls++; return ValueTask.FromResult(1); }));
        Assert.AreEqual(0, blockedCalls);
        Assert.AreEqual(23, await admission.RunAsync(second, _ => ValueTask.FromResult(23)));
        release.SetResult(); Assert.AreEqual(17, await accepted.WaitAsync(TimeSpan.FromSeconds(5)));
        var scope = await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        scope.CompleteAfterFreshAudit(); Assert.IsFalse(admission.IsAdmitted(first));
        await scope.DisposeAsync(); await scope.DisposeAsync(); Assert.IsTrue(admission.IsAdmitted(first));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => Task.Run(scope.CompleteAfterFreshAudit));
    }

    [TestMethod]
    public async Task CanceledDrainKeepsRootsClosedUntilASeparateFreshRecovery()
    {
        RootId root = RootId.New(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(admission, root); using var work = admission.Enter([root]);
        using var stop = new CancellationTokenSource();
        var pending = admission.BeginRecoveryAsync([root], stop.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted); await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
        Assert.IsFalse(admission.IsAdmitted(root)); work.Dispose();
        await using (var failed = await admission.BeginRecoveryAsync([root])) { }
        Assert.IsFalse(admission.IsAdmitted(root)); await OpenAsync(admission, root); Assert.IsTrue(admission.IsAdmitted(root));
    }

    [TestMethod]
    public async Task MultiRootAdmissionIsAtomicAndOverlappingRecoveryCannotStealOwnership()
    {
        RootId first = RootId.New(), second = RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first, second]); await OpenAsync(admission, first, second);
        var recovery = await admission.BeginRecoveryAsync([second]);
        Assert.ThrowsExactly<InvalidOperationException>(() => admission.Enter([first, second]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await admission.BeginRecoveryAsync([first, second]));
        Assert.IsTrue(admission.IsAdmitted(first));
        using (admission.Enter([first])) { }
        recovery.CompleteAfterFreshAudit(); await recovery.DisposeAsync(); Assert.IsTrue(admission.IsAdmitted(second));
    }

    [TestMethod]
    public async Task DisposalDrainsWorkAndRecoveryWithoutReopeningTheOwner()
    {
        RootId first = RootId.New(), second = RootId.New();
        var admission = new MirrorPulseNamespaceWorkAdmission([first, second]); await OpenAsync(admission, first, second);
        using var work = admission.Enter([first]); var recovery = await admission.BeginRecoveryAsync([second]);
        var disposal = admission.DisposeAsync().AsTask(); Assert.IsFalse(disposal.IsCompleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => admission.Enter([first]));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await admission.BeginRecoveryAsync([first]));
        work.Dispose(); Assert.IsFalse(disposal.IsCompleted);
        recovery.CompleteAfterFreshAudit(); await recovery.DisposeAsync();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5)); await admission.DisposeAsync();
        Assert.IsFalse(admission.IsAdmitted(first)); Assert.IsFalse(admission.IsAdmitted(second));
    }

    [TestMethod]
    public async Task PendingDrainAndDisposalBothWaitForTheActualAcceptedWork()
    {
        RootId root = RootId.New(); var admission = new MirrorPulseNamespaceWorkAdmission([root]); await OpenAsync(admission, root);
        var work = admission.Enter([root]); var recovery = admission.BeginRecoveryAsync([root]).AsTask();
        var disposal = admission.DisposeAsync().AsTask(); Assert.IsFalse(disposal.IsCompleted); Assert.IsFalse(recovery.IsCompleted);
        work.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5)); await admission.DisposeAsync();
    }

    [TestMethod]
    public async Task NewRootsAndNewOwnersNeverInheritPriorAdmission()
    {
        RootId root = RootId.New(), extra = RootId.New();
        await using (var admission = new MirrorPulseNamespaceWorkAdmission([root]))
        {
            await OpenAsync(admission, root); admission.Register(root); admission.Register(extra);
            Assert.IsTrue(admission.IsAdmitted(root)); Assert.IsFalse(admission.IsAdmitted(extra));
            Assert.ThrowsExactly<InvalidOperationException>(() => admission.Enter([root, extra]));
            Assert.ThrowsExactly<ArgumentException>(() => admission.Enter([root, root]));
            Assert.ThrowsExactly<ArgumentException>(() => admission.Enter([]));
        }
        await using var nextHost = new MirrorPulseNamespaceWorkAdmission([root, extra]);
        Assert.IsFalse(nextHost.IsAdmitted(root)); Assert.IsFalse(nextHost.IsAdmitted(extra));
    }

    private static async Task OpenAsync(MirrorPulseNamespaceWorkAdmission admission, params RootId[] roots)
    {
        await using var recovery = await admission.BeginRecoveryAsync(roots); recovery.CompleteAfterFreshAudit();
    }
}
