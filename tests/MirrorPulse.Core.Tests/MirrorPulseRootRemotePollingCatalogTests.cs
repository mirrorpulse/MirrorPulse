using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootRemotePollingCatalogTests
{
    [TestMethod]
    public async Task RootOwnersSurviveReopenWithoutReplacingAnotherRootOrTheLegacyOwner()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-poll-catalog", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var instance = InstanceId.New(); var first = RootId.New(); var second = RootId.New(); var other = InstanceId.New();
        var original = Record(instance, first, 1); var sibling = Record(instance, second, 2); var external = Record(other, first, 3);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SaveRootPendingRemoteBatchAsync(original); await catalog.SaveRootPendingRemoteBatchAsync(sibling);
                await catalog.SaveRootPendingRemoteBatchAsync(external); await catalog.SaveRootPendingRemoteBatchAsync(original);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveRootPendingRemoteBatchAsync(original with { Payload = [9] }));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveRootPendingRemoteBatchAsync(original with { CandidateSnapshot = [9] }));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ClearRootPendingRemoteBatchAsync(instance, first, sibling.BatchId));
                Assert.IsNotNull(await catalog.ReadPendingRemoteBatchAsync(instance));
                Assert.IsNull(await catalog.ReadLegacyPendingRemoteBatchAsync(instance));
                Assert.IsNull(await catalog.ReadRootPendingRemoteBatchAsync(other, second));
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var restored = (await reopened.ReadRootPendingRemoteBatchAsync(instance, first))!;
                Assert.AreEqual(first, restored.RootId); Assert.AreEqual(original.BatchId, restored.BatchId);
                CollectionAssert.AreEqual(original.Payload, restored.Payload); CollectionAssert.AreEqual(original.CandidateSnapshot, restored.CandidateSnapshot);
                await reopened.ClearRootPendingRemoteBatchAsync(instance, first, original.BatchId);
                await reopened.ClearRootPendingRemoteBatchAsync(instance, first, original.BatchId);
                Assert.IsNotNull(await reopened.ReadRootPendingRemoteBatchAsync(instance, second));
                Assert.IsNotNull(await reopened.ReadRootPendingRemoteBatchAsync(other, first));
                Assert.IsNotNull(await reopened.ReadPendingRemoteBatchAsync(instance));
                await reopened.ClearRootPendingRemoteBatchAsync(instance, second, sibling.BatchId);
                Assert.IsNull(await reopened.ReadPendingRemoteBatchAsync(instance));
                var legacy = new MirrorPulsePendingRemoteBatchRecord(instance, instance + "/legacy", [4], [5]);
                await reopened.SavePendingRemoteBatchAsync(legacy);
                await reopened.SaveRootPendingRemoteBatchAsync(original);
                Assert.IsNull((await reopened.ReadPendingRemoteBatchAsync(instance))!.RootId);
                await reopened.ClearPendingRemoteBatchAsync(instance, legacy.BatchId);
                Assert.AreEqual(first, (await reopened.ReadPendingRemoteBatchAsync(instance))!.RootId);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("missing-root")]
    [DataRow("other-root")]
    [DataRow("other-instance")]
    [DataRow("empty-payload")]
    [DataRow("empty-snapshot")]
    public async Task InvalidRootIntentCannotAcquireDurableOwnership(string fault)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-poll-invalid", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var original = Record(InstanceId.New(), RootId.New(), 1);
        var invalid = fault switch
        {
            "missing-root" => original with { RootId = null },
            "other-root" => original with { RootId = RootId.New() },
            "other-instance" => original with { InstanceId = InstanceId.New() },
            "empty-payload" => original with { Payload = [] },
            "empty-snapshot" => original with { CandidateSnapshot = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveRootPendingRemoteBatchAsync(invalid));
            Assert.IsNull(await catalog.ReadPendingRemoteBatchAsync(original.InstanceId));
            Assert.IsNull(await catalog.ReadRootPendingRemoteBatchAsync(original.InstanceId, original.RootId!.Value));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SavePendingRemoteBatchAsync(original));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Schema66UpgradePreservesLegacyBytesWithoutInventingARootAttribution()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-poll-migration", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var instance = InstanceId.New();
        var legacy = new MirrorPulsePendingRemoteBatchRecord(instance, instance + "/original", [1, 2, 3], [4, 5, 6]);
        try
        {
            await using (var initial = await MirrorPulseProductCatalog.OpenAsync(paths)) await initial.SavePendingRemoteBatchAsync(legacy);
            await using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await database.OpenAsync(); await using var old = database.CreateCommand();
                old.CommandText = "DROP TABLE root_pending_remote_batches; PRAGMA user_version=66;"; await old.ExecuteNonQueryAsync();
            }
            await using (var upgraded = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var retained = (await upgraded.ReadLegacyPendingRemoteBatchAsync(instance))!;
                Assert.IsNull(retained.RootId); CollectionAssert.AreEqual(legacy.Payload, retained.Payload);
                CollectionAssert.AreEqual(legacy.CandidateSnapshot, retained.CandidateSnapshot);
                Assert.IsNull(await upgraded.ReadRootPendingRemoteBatchAsync(instance, RootId.New()));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static MirrorPulsePendingRemoteBatchRecord Record(InstanceId instance, RootId root, byte value) =>
        new(instance, $"{instance}/poll-root/{root}/batch", [value], [(byte)(value + 1)]) { RootId = root };
}
