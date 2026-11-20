using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using MirrorPulse.Tests.Fixtures;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ASelectedOriginalReceiptFaultCannotBeConsumedByAnEarlierNotification()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceTransferCopy copy;
        MirrorPulseRetiredTransferObservationAcknowledgement earlierReceipt, selectedStart;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            copy = await PrepareCompletedRetiredTransferAsync(catalog);
            var earlier = LateTransferObservation(copy) with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate };
            var selected = earlier with
            {
                OperationId = Guid.NewGuid(),
                Sequence = earlier.Sequence + 1,
                Kind = MirrorPulseWorkerChangeKind.Delete,
                ObservedAt = earlier.ObservedAt.AddSeconds(1)
            };
            await ExecuteSqlAsync(fixture.Paths, TransferAcknowledgementFaultSql.BeforeOriginalReceipt(selected.OperationId));
            try
            {
                var earlierStart = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId,
                    earlier, selected.ObservedAt.AddSeconds(1));
                earlierReceipt = await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(earlierStart,
                    earlierStart.StartedAt.AddSeconds(1));
                Assert.IsNotNull(earlierReceipt.AcknowledgedAt);
                selectedStart = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId,
                    selected, earlierReceipt.AcknowledgedAt.Value.AddSeconds(1));
                await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteRetiredTransferObservationAcknowledgementAsync(
                    selectedStart, selectedStart.StartedAt.AddSeconds(1)));
                Assert.AreEqual(selectedStart, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(selected.OperationId));
                Assert.IsNull(selectedStart.AcknowledgedAt);
                Assert.AreEqual(earlierReceipt, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(earlier.OperationId));
                CollectionAssert.AreEqual(new[] { selected.OperationId },
                    (await catalog.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId))
                        .Select(pending => pending.Observation.OperationId).ToArray());
                Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
                Assert.IsNull(await catalog.ReadWorkerRequestAsync(earlier.OperationId));
                Assert.IsNull(await catalog.ReadWorkerRequestAsync(selected.OperationId));
            }
            finally { await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_transfer_ack;"); }
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(selectedStart, await reopened.ReadRetiredTransferObservationAcknowledgementAsync(selectedStart.Observation.OperationId));
        var completed = await reopened.CompleteRetiredTransferObservationAcknowledgementAsync(selectedStart, selectedStart.StartedAt.AddSeconds(2));
        Assert.IsNotNull(completed.AcknowledgedAt);
        Assert.AreEqual(selectedStart, completed with { AcknowledgedAt = null });
        Assert.AreEqual(earlierReceipt, await reopened.ReadRetiredTransferObservationAcknowledgementAsync(earlierReceipt.Observation.OperationId));
        Assert.IsEmpty(await reopened.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId));
        Assert.AreEqual(copy, await reopened.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); // Real product SQLite boundary; no SDK ACK return is claimed.
    }

    [TestMethod]
    public async Task ABroadReceiptFaultCanThrowBeforeTheSelectedOriginalHasAnyStart()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog);
        var earlier = LateTransferObservation(copy) with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate };
        var selected = earlier with
        {
            OperationId = Guid.NewGuid(),
            Sequence = earlier.Sequence + 1,
            Kind = MirrorPulseWorkerChangeKind.Delete,
            ObservedAt = earlier.ObservedAt.AddSeconds(1)
        };
        // The former native fixture's unfiltered trigger can be consumed by any earlier final receipt.
        await ExecuteSqlAsync(fixture.Paths,
            "CREATE TRIGGER fail_late_transfer_ack BEFORE UPDATE ON retired_transfer_observations BEGIN SELECT RAISE(ABORT,'late-ack-return-gap'); END;");
        try
        {
            var started = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId,
                earlier, selected.ObservedAt.AddSeconds(1));
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteRetiredTransferObservationAcknowledgementAsync(
                started, started.StartedAt.AddSeconds(1)));
            Assert.AreEqual(started, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(earlier.OperationId));
            Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(selected.OperationId));
            Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        }
        finally { await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_transfer_ack;"); }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
