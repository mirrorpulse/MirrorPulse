using CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task RetainedBatchCanResumeWithoutPollingWhileLiveAdmissionStillRequiresAFreshAudit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-poll-recovery", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdmissionRoot("Files"); var source = new FakeDirectorySource();
        var batches = new List<CloudRemoteChangeBatch>();
        bool ordinary = true, replay = false, fail = true;
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var pending = new MirrorPulseCatalogRemotePollPendingStore(catalog);
            var snapshots = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
            await using var admission = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            await OpenPollAdmissionAsync(admission, root.RootId);
            await using var poller = new MirrorPulseActiveRemotePoller(source, [AdmissionAdapter(root)], [root], (_, batch, _) =>
            {
                batches.Add(batch);
                return fail ? ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(new IOException("Fixture after immutable admission."))
                    : ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
            }, snapshotStore: snapshots, pendingStore: pending, namespaceWork: admission,
                mayPoll: (_, _) => ValueTask.FromResult(ordinary), mayReplay: (_, _, _) => ValueTask.FromResult(replay));
            source.Set(new FakeEntry("file", "v1", CloudItemKind.File, "original.bin", 3));
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
            source.Set(new FakeEntry("file", "v2", CloudItemKind.File, "moved.bin", 3));
            await Assert.ThrowsExactlyAsync<IOException>(() => poller.PollOnceAsync(root.InstanceId).AsTask());
            var original = (await pending.LoadAsync(root, default))!;
            int reads = source.Reads;
            ordinary = false; fail = false;
            source.Set(new FakeEntry("file", "v3", CloudItemKind.File, "newer.bin", 3));
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
            Assert.HasCount(1, batches); Assert.AreEqual(reads, source.Reads);
            // A matching durable batch does not turn a closed live root into a ready root.
            replay = true;
            await using (await admission.BeginRecoveryAsync([root.RootId])) { }
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
            Assert.IsFalse(admission.IsAdmitted(root.RootId)); Assert.HasCount(1, batches);
            Assert.AreEqual(original.BatchId, (await pending.LoadAsync(root, default))!.BatchId);
            await OpenPollAdmissionAsync(admission, root.RootId);
            Assert.IsTrue(await poller.PollOnceAsync(root.InstanceId));
            Assert.HasCount(2, batches); Assert.AreEqual(reads, source.Reads);
            Assert.AreEqual(original.BatchId, batches[1].BatchId);
            CollectionAssert.AreEqual(original.Fingerprint, batches[1].Fingerprint.ToArray());
            Assert.AreEqual("v2", (await snapshots.LoadAsync(root))!["file"].RemoteRevision);
            Assert.IsNull(await pending.LoadAsync(root, default));
            Assert.IsFalse(await poller.PollOnceAsync(root.InstanceId));
            Assert.AreEqual(reads, source.Reads); // Ordinary source polling remains fenced.
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
