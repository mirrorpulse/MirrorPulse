using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ClosedFailureObservationPreservesTheExactOriginalExceptionAndDurableReplay(bool failWriter, bool classified)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-poll-diagnostics", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string logDirectory = Path.Combine(directory, "logs");
        InstanceId instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.poll-diagnostic"), instance,
            new AdapterRootDefinition("local", "Local", "Local", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Local",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var source = new FakeDirectorySource();
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
        var failure = new InvalidOperationException(classified ?
            "The remote birth identity is already owned by another original object or birth." : "PRIVATE_PATH password=PRIVATE_VALUE");
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var pending = new MirrorPulseCatalogRemotePollPendingStore(catalog);
            var observed = new List<CloudRemoteChangeBatch>();
            await using var poller = new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
            {
                observed.Add(batch); return ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(failure);
            }, snapshotStore: snapshots, pendingStore: pending, diagnosticsDirectory: logDirectory);
            source.Set(new FakeEntry("original", "v1", CloudItemKind.File, "file.bin", 3));
            Assert.IsFalse(await poller.PollOnceAsync(instance));
            source.Set(new FakeEntry("original", "v2", CloudItemKind.File, "file.bin", 4));
            string logPath = Path.Combine(logDirectory, "mirrorpulse.log");
            using (var blocked = failWriter ? new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.None) : null)
            {
                Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => poller.PollOnceAsync(instance).AsTask()));
                var originalIntent = await pending.LoadAsync(root, CancellationToken.None);
                Assert.IsNotNull(originalIntent);
                Assert.AreEqual("v1", (await snapshots.LoadAsync(root))!["original"].RemoteRevision);
                int reads = source.Reads;
                source.Set(new FakeEntry("original", "v3", CloudItemKind.File, "file.bin", 5));
                Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => poller.PollOnceAsync(instance).AsTask()));
                Assert.AreEqual(reads, source.Reads, "A failure observer must not enumerate a later generation before original replay.");
                Assert.AreEqual(originalIntent.BatchId, (await pending.LoadAsync(root, CancellationToken.None))!.BatchId);
                Assert.HasCount(2, observed); Assert.AreEqual(observed[0].BatchId, observed[1].BatchId);
                CollectionAssert.AreEqual(observed[0].Fingerprint.ToArray(), observed[1].Fingerprint.ToArray());
            }
            if (!failWriter)
            {
                var lines = await File.ReadAllLinesAsync(logPath); Assert.HasCount(2, lines);
                foreach (string line in lines)
                {
                    using var json = JsonDocument.Parse(line);
                    Assert.AreEqual("RemotePollFailed", json.RootElement.GetProperty("Code").GetString());
                    Assert.AreEqual(classified ? "BirthIdentityOwnership" : "Unknown",
                        json.RootElement.GetProperty("Fields").GetProperty("remoteFailureBoundary").GetString());
                    Assert.IsFalse(line.Contains("PRIVATE", StringComparison.Ordinal));
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
