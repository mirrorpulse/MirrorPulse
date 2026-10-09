using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRemotePollPendingStoreTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CatalogReopensIdenticalIntentAndRefusesReplacingOrClearingAnotherBatch(int projectionVersion)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-pending-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        InstanceId instance = InstanceId.New();
        var before = new Dictionary<string, MirrorPulseRemoteSnapshotEntry>
        {
            ["file"] = new("file", "v1", CloudItemKind.File, "file.bin", 1,
                new(CloudItemKind.File, FileAttributes.Archive, null, null, DateTimeOffset.UtcNow, null)),
        };
        var after = new Dictionary<string, MirrorPulseRemoteSnapshotEntry>(before)
        { ["file"] = before["file"] with { RemoteRevision = "v2", Length = 2 } };
        var pending = new MirrorPulsePendingRemotePoll(instance + "/batch", Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            "Files", before, after, projectionVersion);
        try
        {
            byte[] payload;
            byte[] candidate;
            await using (MirrorPulseProductCatalog first = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var store = new MirrorPulseCatalogRemotePollPendingStore(first);
                await store.SaveAsync(instance, pending, CancellationToken.None);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(instance,
                    pending with { ProjectionVersion = 3 }, CancellationToken.None).AsTask());
                await store.SaveAsync(instance, pending, CancellationToken.None);
                MirrorPulsePendingRemoteBatchRecord record = (await first.ReadPendingRemoteBatchAsync(instance))!;
                payload = record.Payload;
                candidate = record.CandidateSnapshot;
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(instance,
                    pending with { BatchId = instance + "/other" }, CancellationToken.None).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ClearAsync(instance, instance + "/other", CancellationToken.None).AsTask());
            }
            await using (MirrorPulseProductCatalog reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var store = new MirrorPulseCatalogRemotePollPendingStore(reopened);
                MirrorPulsePendingRemotePoll restored = (await store.LoadAsync(instance, CancellationToken.None))!;
                Assert.AreEqual(pending.BatchId, restored.BatchId);
                Assert.AreEqual(pending.RootDirectoryName, restored.RootDirectoryName);
                Assert.AreEqual(projectionVersion, restored.ProjectionVersion);
                Assert.AreEqual(before["file"], restored.Previous["file"]);
                Assert.AreEqual(after["file"], restored.Candidate["file"]);
                CollectionAssert.AreEqual(pending.Fingerprint, restored.Fingerprint);
                MirrorPulsePendingRemoteBatchRecord record = (await reopened.ReadPendingRemoteBatchAsync(instance))!;
                CollectionAssert.AreEqual(payload, record.Payload);
                CollectionAssert.AreEqual(candidate, record.CandidateSnapshot);
                await store.ClearAsync(instance, pending.BatchId, CancellationToken.None);
                await store.ClearAsync(instance, pending.BatchId, CancellationToken.None);
                Assert.IsNull(await store.LoadAsync(instance, CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
