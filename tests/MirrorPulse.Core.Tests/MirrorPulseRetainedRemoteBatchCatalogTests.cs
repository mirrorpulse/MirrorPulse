using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRetainedRemoteBatchCatalogTests
{
    [TestMethod]
    public async Task OpaqueIntentReopensAndCanOnlyBeCompletedByItsExactOwnerAndPayload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Batch(); var sibling = fixture.Batch(InstanceId.New());
        await fixture.Catalog.RetainRemoteBatchAsync(original); await fixture.Catalog.RetainRemoteBatchAsync(original);
        await fixture.Catalog.RetainRemoteBatchAsync(sibling);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.RetainRemoteBatchAsync(original with { BatchId = original.InstanceId + "/newer" }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.RetainRemoteBatchAsync(original with { Payload = [9] }));
        await fixture.ReopenAsync();
        var retained = (await fixture.Catalog.ReadRetainedRemoteBatchAsync(original.InstanceId))!;
        Assert.AreEqual(original.BatchId, retained.BatchId); CollectionAssert.AreEqual(original.Payload, retained.Payload);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.CompleteRetainedRemoteBatchAsync(original with { Payload = [9] }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.CompleteRetainedRemoteBatchAsync(original with { BatchId = original.InstanceId + "/different" }));
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(original); await fixture.Catalog.CompleteRetainedRemoteBatchAsync(original);
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(original.InstanceId));
        Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(sibling.InstanceId));
    }

    [TestMethod]
    public async Task PendingOwnerDiscoveryPaginatesBeyondTwoPagesWithoutSkippingOrRepeatingOwners()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instances = Enumerable.Range(0, 135).Select(_ => InstanceId.New()).ToArray();
        foreach (var instance in instances) await fixture.Catalog.RetainRemoteBatchAsync(fixture.Batch(instance));
        await fixture.ReopenAsync();
        var seen = new List<InstanceId>(); InstanceId? after = null;
        while (true)
        {
            var page = await fixture.Catalog.ReadRetainedRemoteBatchOwnersAsync(after);
            Assert.IsLessThanOrEqualTo(64, page.Count);
            if (page.Count == 0) break;
            seen.AddRange(page); after = page[^1];
        }
        CollectionAssert.AreEqual(instances.OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray(), seen.ToArray());
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("batch")]
    [DataRow("empty")]
    [DataRow("oversized")]
    public async Task InvalidOrUnboundedPayloadDoesNotAcquireTheInstanceSlot(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(); var original = fixture.Batch();
        var invalid = fault switch
        {
            "instance" => original with { InstanceId = default },
            "batch" => original with { BatchId = InstanceId.New() + "/foreign" },
            "empty" => original with { Payload = [] },
            "oversized" => original with { Payload = new byte[32 * 1024 * 1024 + 1] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.RetainRemoteBatchAsync(invalid));
        Assert.IsEmpty(await fixture.Catalog.ReadRetainedRemoteBatchOwnersAsync());
    }

    [TestMethod]
    public async Task CorruptedReplayPayloadIsRejectedOnReadAndCannotBeClearedAsSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(); var original = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(original);
        await fixture.ExecuteSqlAsync("UPDATE retained_remote_batches SET payload=X'01';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadRetainedRemoteBatchAsync(original.InstanceId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.CompleteRetainedRemoteBatchAsync(original));
    }

    [TestMethod]
    public async Task Schema67UpgradeRetainsPollingOwnersWithoutInventingAnIncomingEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync(); var instance = InstanceId.New(); var root = RootId.New();
        var legacy = new MirrorPulsePendingRemoteBatchRecord(instance, instance + "/legacy", [1, 2], [3]);
        var scoped = new MirrorPulsePendingRemoteBatchRecord(instance, $"{instance}/poll-root/{root}/original", [4], [5]) { RootId = root };
        await fixture.Catalog.SavePendingRemoteBatchAsync(legacy); await fixture.Catalog.SaveRootPendingRemoteBatchAsync(scoped);
        await fixture.Catalog.DisposeAsync();
        await fixture.ExecuteSqlAsync("DROP TABLE retained_remote_batches; PRAGMA user_version=67;");
        fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(instance));
        CollectionAssert.AreEqual(legacy.Payload, (await fixture.Catalog.ReadLegacyPendingRemoteBatchAsync(instance))!.Payload);
        CollectionAssert.AreEqual(scoped.Payload, (await fixture.Catalog.ReadRootPendingRemoteBatchAsync(instance, root))!.Payload);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryStatusPreservesExistingRescanLastSuccessAndTransferEvidence(bool rescan)
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        var time = DateTimeOffset.UtcNow;
        var before = new MirrorPulseInstanceRuntimeState(pending.InstanceId, "Uploading", rescan, time, "ExistingError",
            new("upload", 17, 99, time));
        await fixture.Catalog.SaveInstanceRuntimeStateAsync(before);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        var after = (await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId))!;
        Assert.AreEqual("Remote recovery pending", after.Phase); Assert.AreEqual("RemoteBatchRecoveryPending", after.LastErrorCode);
        Assert.AreEqual(before.RequiresFullRescan, after.RequiresFullRescan); Assert.AreEqual(before.LastSuccessfulSync, after.LastSuccessfulSync);
        Assert.AreEqual(before.TransferProgress, after.TransferProgress); Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(pending.InstanceId));
    }

    [TestMethod]
    public async Task RecoveryStatusCanBeCreatedWithoutAnExistingRuntimeRow()
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        var status = (await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId))!;
        Assert.AreEqual("RemoteBatchRecoveryPending", status.LastErrorCode); Assert.IsFalse(status.RequiresFullRescan);
        Assert.IsNull(status.LastSuccessfulSync); Assert.IsNull(status.TransferProgress);
    }

    [TestMethod]
    public async Task ACompletedOwnerCannotReintroduceAStaleRecoveryStatus()
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(pending);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        Assert.IsNull(await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId));
        var active = new MirrorPulseInstanceRuntimeState(pending.InstanceId, "Idle", true, DateTimeOffset.UtcNow, "RescanPending");
        await fixture.Catalog.SaveInstanceRuntimeStateAsync(active);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        Assert.AreEqual(active, await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulCompletionClearsOnlyItsOwnedRecoveryErrorAndPreservesRescanEvidence(bool rescan)
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        var time = DateTimeOffset.UtcNow; var transfer = new MirrorPulseTransferProgress("upload", 17, 99, time);
        await fixture.Catalog.SaveInstanceRuntimeStateAsync(new(pending.InstanceId, "Uploading", rescan, time, TransferProgress: transfer));
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(pending);
        var state = (await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId))!;
        Assert.IsNull(state.LastErrorCode); Assert.AreEqual(rescan ? "Rescan pending" : "Idle", state.Phase);
        Assert.AreEqual(rescan, state.RequiresFullRescan); Assert.AreEqual(time, state.LastSuccessfulSync); Assert.AreEqual(transfer, state.TransferProgress);
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(pending.InstanceId));
    }

    [TestMethod]
    public async Task CompletionNeverClearsAnotherComponentsLaterErrorOrPhase()
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        var later = new MirrorPulseInstanceRuntimeState(pending.InstanceId, "Worker offline", true, DateTimeOffset.UtcNow, "WorkerUnavailable");
        await fixture.Catalog.SaveInstanceRuntimeStateAsync(later);
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(pending);
        Assert.AreEqual(later, await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId));
    }

    [TestMethod]
    public async Task FailedStatusCompletionRollsBackTheIntentRemovalInTheSameSqliteTransaction()
    {
        await using var fixture = await Fixture.CreateAsync(); var pending = fixture.Batch(); await fixture.Catalog.RetainRemoteBatchAsync(pending);
        await fixture.Catalog.MarkRetainedRemoteBatchRecoveryPendingAsync(pending.InstanceId);
        await fixture.ExecuteSqlAsync("""
            CREATE TRIGGER reject_recovery_completion BEFORE UPDATE OF last_error_code ON instance_runtime
            WHEN OLD.last_error_code='RemoteBatchRecoveryPending' AND NEW.last_error_code IS NULL
            BEGIN SELECT RAISE(ABORT,'Injected status completion interruption.'); END;
            """);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Catalog.CompleteRetainedRemoteBatchAsync(pending));
        CollectionAssert.AreEqual(pending.Payload, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(pending.InstanceId))!.Payload);
        Assert.AreEqual("RemoteBatchRecoveryPending", (await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId))!.LastErrorCode);
        await fixture.ExecuteSqlAsync("DROP TRIGGER reject_recovery_completion;");
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(pending);
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(pending.InstanceId));
        Assert.IsNull((await fixture.Catalog.ReadInstanceRuntimeStateAsync(pending.InstanceId))!.LastErrorCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-retained-remote", Guid.NewGuid().ToString("N"));
        private readonly InstanceId _instance = InstanceId.New();
        internal MirrorPulseStoragePaths Paths { get; private set; } = null!;
        internal MirrorPulseProductCatalog Catalog { get; set; } = null!;
        internal static async Task<Fixture> CreateAsync()
        { var fixture = new Fixture(); fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data")); fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); return fixture; }
        internal MirrorPulseRetainedRemoteBatch Batch(InstanceId? instance = null)
        { var owner = instance ?? _instance; return new(owner, owner + "/original", [1, 2, 3]); }
        internal async Task ExecuteSqlAsync(string text)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = text; await command.ExecuteNonQueryAsync();
        }
        internal async Task ReopenAsync() { await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths); }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
