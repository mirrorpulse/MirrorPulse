using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseDemandAdmissionTests
{
    [TestMethod]
    public async Task ClosedRootRejectsSourceCallsAndReservesRootPublicationForControlledStartup()
    {
        var (router, root) = Router(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        var ranges = new Ranges(_ => ValueTask.FromResult<Stream>(new MemoryStream([7, 8])));
        var pages = new Pages(_ => ValueTask.FromResult(new CloudRemoteDirectoryPage([])));
        var provider = new MirrorPulseDemandProvider(router, ranges, pages, admission);
        byte[] identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "note", "v1").Encode();
        var topLevel = await provider.FetchChildrenAsync(string.Empty, ReadOnlyMemory<byte>.Empty, null);
        Assert.IsEmpty(topLevel.Children); Assert.IsTrue(topLevel.IsComplete);
        Assert.HasCount(1, router.CreateRootPage().Children);
        Assert.IsFalse(admission.IsAdmitted(root.RootId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.FetchChildrenAsync("Docs/note.txt", identity, null).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.OpenReadAsync("Docs/note.txt", identity, 2, 0, 2).AsTask());
        Assert.AreEqual(0, pages.Calls); Assert.AreEqual(0, ranges.Calls);
    }

    [TestMethod]
    public async Task RecoveryDrainsTheActualDirectoryPageCallbackBeforeAcceptingItsAudit()
    {
        var (router, root) = Router(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await OpenAsync(admission, root.RootId);
        var entered = Signal(); var release = new TaskCompletionSource<CloudRemoteDirectoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = new Pages(async _ => { entered.SetResult(); return await release.Task; });
        var provider = new MirrorPulseDemandProvider(router, new Ranges(_ => throw new AssertFailedException("No range expected.")), pages, admission);
        byte[] identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "folder", "v1").Encode();
        var fetching = provider.FetchChildrenAsync("Docs/folder", identity, null).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovery = admission.BeginRecoveryAsync([root.RootId]).AsTask(); Assert.IsFalse(recovery.IsCompleted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.FetchChildrenAsync("Docs/folder", identity, null).AsTask());
        release.SetResult(new CloudRemoteDirectoryPage([])); await fetching.WaitAsync(TimeSpan.FromSeconds(5));
        await using var scope = await recovery.WaitAsync(TimeSpan.FromSeconds(5)); Assert.AreEqual(1, pages.Calls);
        Assert.IsFalse(admission.IsAdmitted(root.RootId)); scope.CompleteAfterFreshAudit();
    }

    [TestMethod]
    public async Task LazyRangeAdmissionSurvivesConcurrentDisposalUntilTheSourceBodyActuallyDisposes()
    {
        var (router, root) = Router(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await OpenAsync(admission, root.RootId);
        var disposingBody = Signal(); var releaseBody = Signal();
        var ranges = new Ranges(_ => ValueTask.FromResult<Stream>(new DelayedDisposalStream(disposingBody, releaseBody)));
        var provider = new MirrorPulseDemandProvider(router, ranges, namespaceWork: admission);
        byte[] identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "note", "v1").Encode();
        await using Stream stream = await provider.OpenReadAsync("Docs/note.txt", identity, 2, 0, 2);
        Assert.AreEqual(0, ranges.Calls); // Opening the provider body stays lazy.
        var recovery = admission.BeginRecoveryAsync([root.RootId]).AsTask(); Assert.IsFalse(recovery.IsCompleted);
        byte[] bytes = new byte[2]; var reading = stream.ReadAsync(bytes).AsTask();
        await disposingBody.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = stream.DisposeAsync().AsTask(); Assert.IsFalse(disposal.IsCompleted); Assert.IsFalse(recovery.IsCompleted);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[1]).AsTask());
        releaseBody.SetResult(); Assert.AreEqual(2, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await using var scope = await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, bytes); Assert.AreEqual(1, ranges.Calls);
        Assert.IsFalse(admission.IsAdmitted(root.RootId)); scope.CompleteAfterFreshAudit();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrCanceledRangesKeepAdmissionUntilTheConsumerClosesTheBody(bool canceled)
    {
        var (router, root) = Router(); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await OpenAsync(admission, root.RootId);
        var ranges = new Ranges(_ => canceled ? ValueTask.FromCanceled<Stream>(new CancellationToken(true)) :
            ValueTask.FromException<Stream>(new IOException("Fixture range failure.")));
        var provider = new MirrorPulseDemandProvider(router, ranges, namespaceWork: admission);
        byte[] identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "note", "v1").Encode();
        await using Stream stream = await provider.OpenReadAsync("Docs/note.txt", identity, 2, 0, 2);
        var recovery = admission.BeginRecoveryAsync([root.RootId]).AsTask();
        if (canceled) await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => stream.ReadAsync(new byte[2]).AsTask());
        else await Assert.ThrowsExactlyAsync<IOException>(() => stream.ReadAsync(new byte[2]).AsTask());
        Assert.IsFalse(recovery.IsCompleted); await stream.DisposeAsync();
        await using var scope = await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(admission.IsAdmitted(root.RootId)); Assert.AreEqual(1, ranges.Calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task OpenAsync(MirrorPulseNamespaceWorkAdmission admission, RootId root)
    {
        await using var scope = await admission.BeginRecoveryAsync([root]); scope.CompleteAfterFreshAudit();
    }
    private static (MirrorPulseRootRouter Router, RootRegistration Root) Router()
    {
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.admission"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        return (new(Path.Combine(Path.GetTempPath(), "MirrorPulse-demand-admission", Guid.NewGuid().ToString("N")), [root]), root);
    }
    private sealed class Pages(Func<CancellationToken, ValueTask<CloudRemoteDirectoryPage>> fetch) : IMirrorPulseDirectoryPageSource
    {
        public int Calls { get; private set; }
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string path, ReadOnlyMemory<byte> cursor,
            int pageSize, CancellationToken cancellationToken)
        { Calls++; return fetch(cancellationToken); }
    }
    private sealed class Ranges(Func<CancellationToken, ValueTask<Stream>> read) : IMirrorPulseWorkerRangeTransport
    {
        public int Calls { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        { Calls++; return read(cancellationToken); }
    }
    private sealed class DelayedDisposalStream(TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream(new byte[] { 7, 8 })
    {
        public override async ValueTask DisposeAsync()
        {
            entered.TrySetResult(); await release.Task; await base.DisposeAsync();
        }
    }
}
