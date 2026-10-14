using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task ClosedRootsNeverReachThePollSourceAndAnotherInstanceStillProgresses()
    {
        var first = AdmissionRoot("First"); var second = AdmissionRoot("Second");
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first.RootId, second.RootId]);
        var source = new FakeDirectorySource(); source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
        int applies = 0;
        await using var poller = new MirrorPulseActiveRemotePoller(source, [AdmissionAdapter(first), AdmissionAdapter(second)], [first, second],
            (_, batch, _) => { applies++; return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor)); }, namespaceWork: admission);
        Assert.IsFalse(await poller.PollOnceAsync(first.InstanceId)); Assert.AreEqual(0, source.Reads);
        await OpenPollAdmissionAsync(admission, second.RootId);
        Assert.IsFalse(await poller.PollOnceAsync(second.InstanceId)); Assert.AreEqual(1, source.Reads);
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
        Assert.IsFalse(await poller.PollOnceAsync(first.InstanceId)); Assert.AreEqual(1, source.Reads);
        Assert.IsTrue(await poller.PollOnceAsync(second.InstanceId)); Assert.AreEqual(1, applies);
        Assert.IsFalse(admission.IsAdmitted(first.RootId));
    }

    [TestMethod]
    public async Task RecoveryWaitsForRemoteApplyAndSnapshotProjectionBeforeReleasingPollingWork()
    {
        var root = AdmissionRoot("Files"); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await OpenPollAdmissionAsync(admission, root.RootId);
        var source = new FakeDirectorySource(); source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new AdmissionSnapshotStore();
        await using var poller = new MirrorPulseActiveRemotePoller(source, [AdmissionAdapter(root)], [root], async (_, batch, _) =>
        {
            applying.SetResult(); await releaseApply.Task; return new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor);
        }, snapshotStore: store, namespaceWork: admission);
        Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
        var polling = poller.PollOnceAsync(root.InstanceId).AsTask(); await applying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovering = admission.BeginRecoveryAsync([root.RootId]).AsTask(); Assert.IsFalse(recovering.IsCompleted);
        int calls = source.Reads; var queued = poller.PollOnceAsync(root.InstanceId).AsTask(); Assert.IsFalse(queued.IsCompleted);
        releaseApply.SetResult(); await store.Projecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(recovering.IsCompleted); Assert.IsFalse(polling.IsCompleted);
        store.Release.SetResult(); Assert.IsTrue(await polling.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(await queued.WaitAsync(TimeSpan.FromSeconds(5))); Assert.AreEqual(calls, source.Reads);
        await using var scope = await recovering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("v2", store.Snapshot!.Values.Single().RemoteRevision); Assert.IsFalse(admission.IsAdmitted(root.RootId));
        scope.CompleteAfterFreshAudit();
    }

    [TestMethod]
    public async Task AFailedPollReleasesItsWorkWithoutOpeningTheRecoveryRoot()
    {
        var root = AdmissionRoot("Files"); await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await OpenPollAdmissionAsync(admission, root.RootId);
        var source = new FakeDirectorySource(); source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "file.bin", 3));
        await using var poller = new MirrorPulseActiveRemotePoller(source, [AdmissionAdapter(root)], [root],
            (_, _, _) => ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(new IOException("Fixture apply failure.")), namespaceWork: admission);
        Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
        source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "file.bin", 4));
        await Assert.ThrowsExactlyAsync<IOException>(() => poller.PollOnceAsync(root.InstanceId).AsTask());
        await using var scope = await admission.BeginRecoveryAsync([root.RootId]); Assert.IsFalse(admission.IsAdmitted(root.RootId));
    }

    private static RootRegistration AdmissionRoot(string name) => AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.poll-admission"), InstanceId.New(),
        new AdapterRootDefinition("files", name, name, false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
    private static AdapterInstance AdmissionAdapter(RootRegistration root) => new(root.AdapterId, InstallId.New(), root.InstanceId, root.Label,
        new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
    private static async Task OpenPollAdmissionAsync(MirrorPulseNamespaceWorkAdmission admission, RootId root)
    {
        await using var scope = await admission.BeginRecoveryAsync([root]); scope.CompleteAfterFreshAudit();
    }
    private sealed class AdmissionSnapshotStore : IMirrorPulseRemotePollSnapshotStore
    {
        public IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>? Snapshot { get; private set; }
        public TaskCompletionSource Projecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>?> LoadAsync(InstanceId instanceId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
        public async ValueTask SaveAsync(InstanceId instanceId, IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Values.Any(entry => entry.RemoteRevision == "v2")) { Projecting.SetResult(); await Release.Task.WaitAsync(cancellationToken); }
            Snapshot = snapshot;
        }
    }
}
