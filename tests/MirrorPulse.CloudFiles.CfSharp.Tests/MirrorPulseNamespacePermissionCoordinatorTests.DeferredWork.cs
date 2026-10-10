using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    public async Task DeferredWorkCannotAcquireNativeResourcesDuringReopenedCapture()
    {
        using var fixture = new Fixture();
        var definition = Capture(fixture);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await catalog.CreateNamespacePermissionTreeAsync(definition);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(reopened);
        int opens = 0;
        Task<MirrorPulseNamespacePermissionBaseline> Open(MirrorPulseNamespacePermissionChange change,
            MirrorPulseNamespacePermissionBaseline original, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            opens++;
            Assert.AreEqual(fixture.Intent, change.Intent);
            return Task.FromResult(original);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.RunAdmittedPermissionWorkAsync(fixture.Intent.OperationId, Open));
        Assert.AreEqual(0, opens);
        await reopened.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, DateTimeOffset.UtcNow);
        Assert.AreEqual(fixture.Baseline, await coordinator.RunAdmittedPermissionWorkAsync(fixture.Intent.OperationId, Open));
        Assert.AreEqual(1, opens);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await reopened.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    public async Task DeferredWorkStartsAfterGateAndReadsTheLatestRetainedPhase()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new CallbackRelease(release);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent)
        {
            BeforeWrite = async () => { entered.TrySetResult(); await release.Task; },
        };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Task<MirrorPulseNamespacePermissionResult> earlier = coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int opens = 0;
        Task<MirrorPulseNamespacePermissionChange> queued = coordinator.RunAdmittedPermissionWorkAsync(fixture.Intent.OperationId,
            (change, original, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                opens++;
                Assert.AreEqual(fixture.Baseline, original);
                Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, change.Phase);
                return Task.FromResult(change);
            });
        Assert.AreEqual(0, opens);
        Assert.IsFalse(queued.IsCompleted);
        release.TrySetResult();
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await earlier.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        Assert.AreEqual(await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId), await queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, opens);
        Assert.AreEqual(1, lease.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredScopeDrainsBeforeRootOrParentCaptureAndOwnerDisposal(bool parent)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var preparation = parent ? Parent(fixture) : new(fixture.Baseline, fixture.Intent);
        await catalog.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new CallbackRelease(release);
        using var stop = new CancellationTokenSource();
        var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Task<Guid> work = coordinator.RunAdmittedPermissionWorkAsync(preparation.Intent.OperationId,
            async (change, original, token) =>
            {
                Assert.AreEqual(preparation.Baseline, original);
                using var signal = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                return change.Intent.OperationId;
            }, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var definition = Capture(fixture);
        Task<MirrorPulseNamespacePermissionTree> capture = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => { defined.TrySetResult(); return Task.FromResult(definition); }, async (tree, token) =>
                await catalog.CancelNamespacePermissionTreeCaptureAsync(tree.Definition.ManifestId, DateTimeOffset.UtcNow, token));
        stop.Cancel();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = coordinator.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(work.IsCompleted);
            Assert.IsFalse(defined.Task.IsCompleted);
            Assert.IsFalse(disposal.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => coordinator.RunAdmittedPermissionWorkAsync(preparation.Intent.OperationId,
                (_, _, _) => Task.FromResult(Guid.Empty)));
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(preparation.Intent.OperationId, await work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, (await capture.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(preparation.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    public async Task CanceledQueuedWorkNeverAcquiresNativeResourcesAndDoesNotKeepCaptureFenced()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new CallbackRelease(release);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent)
        {
            BeforeWrite = async () => { entered.TrySetResult(); await release.Task; },
        };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Task<MirrorPulseNamespacePermissionResult> earlier = coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource();
        int opens = 0;
        Task<Guid> queued = coordinator.RunAdmittedPermissionWorkAsync(fixture.Intent.OperationId,
            (_, _, _) => { opens++; return Task.FromResult(Guid.Empty); }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.AreEqual(0, opens);
        release.TrySetResult();
        await earlier.WaitAsync(TimeSpan.FromSeconds(2));
        var definition = Capture(fixture);
        var capture = await coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition), _ => Task.FromResult(definition),
            async (tree, token) => await catalog.CancelNamespacePermissionTreeCaptureAsync(tree.Definition.ManifestId, DateTimeOffset.UtcNow, token))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, capture.Phase);
        Assert.AreEqual(0, opens);
    }

    [TestMethod]
    public async Task FailedDeferredWorkReleasesAdmissionWithoutChangingOriginalsOrIntent()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var failure = new IOException("Synthetic scope acquisition failure before native changes.");
        var observed = await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.RunAdmittedPermissionWorkAsync<Guid>(fixture.Intent.OperationId,
            (_, _, _) => throw failure));
        Assert.AreSame(failure, observed);
        Assert.AreEqual(prepared, await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId));
        Assert.AreEqual(fixture.Baseline, await catalog.ReadNamespacePermissionBaselineAsync(fixture.Baseline.EvidenceId));
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(fixture.Intent.OperationId, lease)).Outcome);
        Assert.AreEqual(1, lease.Writes);
    }
}
