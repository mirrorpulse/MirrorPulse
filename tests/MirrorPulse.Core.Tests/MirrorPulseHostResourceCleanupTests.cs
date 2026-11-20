using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseHostResourceCleanupTests
{
    private static readonly string[] Resources = ["poller", "actor", "session", "workers", "catalog"];

    [TestMethod]
    [DataRow("poller")]
    [DataRow("actor")]
    [DataRow("session")]
    [DataRow("workers")]
    [DataRow("catalog")]
    public async Task OneFailedResourceDoesNotSkipLaterOwnersAndPreservesTheOriginalException(string failed)
    {
        var disposed = new List<string>(); var original = new IOException("Synthetic resource failure.");
        var cleanup = Resources.Select(resource => (Func<ValueTask>)(async () =>
        {
            await Task.Yield(); disposed.Add(resource); if (resource == failed) throw original;
        })).ToArray();
        var actual = await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseHostResourceCleanup.RunAsync(cleanup));
        Assert.AreSame(original, actual); CollectionAssert.AreEqual(Resources, disposed);
    }

    [TestMethod]
    public async Task StartupAndSynchronousAndAsynchronousCleanupFailuresRemainOrderedAfterEveryOwnerIsAttempted()
    {
        var startup = new InvalidDataException("Synthetic startup failure."); var first = new IOException("Synthetic synchronous failure.");
        var second = new UnauthorizedAccessException("Synthetic asynchronous failure."); bool lastDisposed = false;
        var actual = await Assert.ThrowsExactlyAsync<AggregateException>(() => MirrorPulseHostResourceCleanup.RunAsync([
            () => throw first,
            async () => { await Task.Yield(); throw second; },
            () => { lastDisposed = true; return ValueTask.CompletedTask; }
        ], startup));
        Assert.IsTrue(lastDisposed); Assert.HasCount(3, actual.InnerExceptions);
        Assert.AreSame(startup, actual.InnerExceptions[0]); Assert.AreSame(first, actual.InnerExceptions[1]); Assert.AreSame(second, actual.InnerExceptions[2]);
        Assert.AreSame(startup, await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseHostResourceCleanup.RunAsync([], startup)));
    }

    [TestMethod]
    public async Task AnEarlierFailureStillAwaitsTheActualNamespaceActorBeforeDisposingItsLibrary()
    {
        await using var actor = new MirrorPulseNamespaceActor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool callbackExited = false, libraryDisposed = false; var original = new IOException("Synthetic poller failure.");
        Task<bool> callback = actor.RunAsync(async _ => { entered.SetResult(); await release.Task; callbackExited = true; return true; }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task cleanup = MirrorPulseHostResourceCleanup.RunAsync([
            () => throw original, actor.DisposeAsync,
            () => { Assert.IsTrue(callbackExited); libraryDisposed = true; return ValueTask.CompletedTask; }
        ]);
        try
        {
            Assert.IsFalse(cleanup.IsCompleted); Assert.IsFalse(libraryDisposed);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await actor.RunAsync(_ => ValueTask.FromResult(true)));
        }
        finally { release.TrySetResult(); }
        Assert.IsTrue(await callback.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(() => cleanup.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.IsTrue(libraryDisposed);
    }

    [TestMethod]
    public async Task ClosingTheActualAdmissionOwnerDrainsItsLeaseBeforeTheCatalogCanClose()
    {
        RootId root = RootId.New(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await using (var recovery = await admission.BeginRecoveryAsync([root])) recovery.CompleteAfterFreshAudit();
        var lease = admission.Enter([root]); bool catalogClosed = false;
        Task cleanup = MirrorPulseHostResourceCleanup.RunAsync([
            admission.DisposeAsync,
            () => { catalogClosed = true; return ValueTask.CompletedTask; }
        ]);
        try { Assert.IsFalse(cleanup.IsCompleted); Assert.IsFalse(catalogClosed); Assert.IsFalse(admission.IsAdmitted(root)); }
        finally { lease.Dispose(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsTrue(catalogClosed);
    }
}
