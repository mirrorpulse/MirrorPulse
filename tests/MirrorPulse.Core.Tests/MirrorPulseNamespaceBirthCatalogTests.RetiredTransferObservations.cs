using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("insert")]
    [DataRow("delete")]
    [DataRow("update")]
    public async Task RetiredTransferObservationSchemaRejectsReplacedNameEpochTriggers(string boundary)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await PrepareCompletedRetiredTransferAsync(catalog);
        string action = boundary.ToUpperInvariant();
        await ExecuteSqlAsync(fixture.Paths, $"DROP TRIGGER namespace_name_epoch_{boundary}; CREATE TRIGGER namespace_name_epoch_{boundary} AFTER {action} ON namespace_member_names BEGIN SELECT 1; END;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow((int)MirrorPulseLocalOperationBlockReason.RootReconciliationRequired, false)]
    [DataRow((int)MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch, false)]
    [DataRow((int)MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, true)]
    public async Task RetiredTransferObservationCompletionClearsOnlyItsResolvedOwnershipBlockAtomically(int reason, bool retained)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        var start = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        await catalog.SaveBlockedLocalOperationAsync(new(observation.OperationId, copy.Plan.SourceRoute.InstanceId,
            observation.RelativePath, (MirrorPulseLocalOperationBlockReason)reason, observation.ObservedAt));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_late_block_clear BEFORE DELETE ON blocked_local_operations BEGIN SELECT RAISE(ABORT,'late-block-clear-gap'); END;");
        if (!retained)
        {
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start, start.StartedAt.AddSeconds(1)));
            Assert.AreEqual(start, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
            Assert.HasCount(1, await catalog.ReadBlockedLocalOperationsAsync());
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_block_clear;");
        await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start, start.StartedAt.AddSeconds(2));
        Assert.HasCount(retained ? 1 : 0, await catalog.ReadBlockedLocalOperationsAsync());
    }

    [TestMethod]
    [DataRow((int)MirrorPulseWorkerChangeKind.Create)]
    [DataRow((int)MirrorPulseWorkerChangeKind.ContentUpdate)]
    [DataRow((int)MirrorPulseWorkerChangeKind.MetadataUpdate)]
    [DataRow((int)MirrorPulseWorkerChangeKind.Delete)]
    public async Task RetiredTransferObservationKeepsItsSeparateFirstAckAcrossCatalogOwners(int kind)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; MirrorPulseRetiredTransferObservationAcknowledgement start;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            copy = await PrepareCompletedRetiredTransferAsync(first);
            var candidates = await first.ReadNamespaceTransferSourceGenerationsAsync(copy.Plan.Source.Identity.ItemId!.Value);
            Assert.HasCount(1, candidates); Assert.AreEqual(copy, candidates[0].Transfer);
            var observation = LateTransferObservation(copy) with { Kind = (MirrorPulseWorkerChangeKind)kind };
            var before = await ReadTransferPayloadHexAsync(fixture.Paths);
            start = await first.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
            Assert.IsNull(start.AcknowledgedAt); Assert.AreEqual(observation, start.Observation);
            Assert.AreEqual(start, await first.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, start.StartedAt.AddSeconds(1)));
            Assert.HasCount(1, await first.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId));
            Assert.IsEmpty(await first.ReadPendingRetiredTransferObservationsAsync(copy.Plan.TargetRoute.RootId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.TryRecordWorkerRequestAsync(
                observation.OperationId, copy.Plan.SourceRoute.InstanceId, new byte[32]));
            Assert.IsNull(await first.ReadWorkerRequestAsync(observation.OperationId));
            Assert.AreEqual(before, await ReadTransferPayloadHexAsync(fixture.Paths));
            Assert.AreEqual(copy.JournalClaim, (await first.ReadNamespaceTransferAsync(copy.Plan.OperationId))!.JournalClaim);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await second.ReadRetiredTransferObservationAcknowledgementAsync(start.Observation.OperationId));
        var completed = await second.CompleteRetiredTransferObservationAcknowledgementAsync(start, start.StartedAt.AddSeconds(2));
        Assert.IsNotNull(completed.AcknowledgedAt);
        Assert.AreEqual(completed, await second.CompleteRetiredTransferObservationAcknowledgementAsync(start, DateTimeOffset.MinValue));
        Assert.IsEmpty(await second.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId));
        Assert.IsEmpty(await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(copy, await second.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); // Product-boundary fixture; no SDK return is claimed.
    }

    [TestMethod]
    [DataRow("unassigned")]
    [DataRow("another-item")]
    [DataRow("another-path")]
    [DataRow("path-case")]
    [DataRow("move")]
    [DataRow("directory")]
    [DataRow("previous-path")]
    [DataRow("sequence")]
    [DataRow("observation-time")]
    [DataRow("original-claimed-id")]
    public async Task RetiredTransferObservationCannotAdoptUnassignedForeignOrOriginalClaimedWork(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        observation = failure switch
        {
            "unassigned" => observation with { ItemId = null },
            "another-item" => observation with { ItemId = Guid.NewGuid() },
            "another-path" => observation with { RelativePath = "Docs/other" },
            "path-case" => observation with { RelativePath = observation.RelativePath.ToUpperInvariant() },
            "move" => observation with { Kind = MirrorPulseWorkerChangeKind.Move },
            "directory" => observation with { IsDirectory = true },
            "previous-path" => observation with { PreviousRelativePath = observation.RelativePath },
            "sequence" => observation with { Sequence = 0 },
            "observation-time" => observation with { ObservedAt = observation.ObservedAt.AddDays(1) },
            _ => observation with { OperationId = copy.JournalClaim!.Members[0].OperationId }
        };
        var start = copy.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(3);
        if (failure == "original-claimed-id")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, start));
        else await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, start));
        Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        Assert.IsEmpty(await catalog.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId));
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
    }

    [TestMethod]
    public async Task RetiredTransferObservationFirstStartAndAckReturnRollbackWithoutLosingOriginalOwnership()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy); var time = observation.ObservedAt.AddSeconds(1);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_late_start BEFORE INSERT ON retired_transfer_observations BEGIN SELECT RAISE(ABORT,'late-start-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, time));
        Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_start;");
        var start = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, time);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_late_return BEFORE UPDATE ON retired_transfer_observations BEGIN SELECT RAISE(ABORT,'late-return-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start, time.AddSeconds(1)));
        Assert.AreEqual(start, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TryRecordWorkerRequestAsync(observation.OperationId, copy.Plan.SourceRoute.InstanceId, new byte[32]));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_late_return;");
        Assert.IsNotNull((await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start, time.AddSeconds(2))).AcknowledgedAt);
    }

    [TestMethod]
    [DataRow("sequence")]
    [DataRow("kind")]
    [DataRow("path-spelling")]
    [DataRow("first-start")]
    public async Task RetiredTransferObservationCannotReplaceItsOriginalRawNotificationOrAckInvocation(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        var start = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        if (change == "first-start")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start with { StartedAt = start.StartedAt.AddTicks(1) }, start.StartedAt.AddSeconds(2)));
        else
        {
            var changed = change switch
            {
                "sequence" => observation with { Sequence = observation.Sequence + 1 },
                "kind" => observation with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate },
                _ => observation with { RelativePath = observation.RelativePath.Replace('/', '\\') }
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, changed, start.StartedAt.AddSeconds(1)));
        }
        Assert.AreEqual(start, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("generation-index")]
    [DataRow("pending-index")]
    [DataRow("epoch-table")]
    [DataRow("epoch-trigger")]
    public async Task RetiredTransferObservationStampedSchemaCannotRecreateLostHistoryOrLookup(string missing)
    {
        using var fixture = new Fixture();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var copy = await PrepareCompletedRetiredTransferAsync(first); var observation = LateTransferObservation(copy);
            await first.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        }
        await ExecuteSqlAsync(fixture.Paths, missing switch
        {
            "table" => "DROP TABLE retired_transfer_observations;",
            "generation-index" => "DROP INDEX namespace_transfer_source_items;",
            "epoch-table" => "DROP TABLE namespace_name_epochs;",
            "epoch-trigger" => "DROP TRIGGER namespace_name_epoch_insert;",
            _ => "DROP INDEX retired_transfer_observation_pending;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task RetiredTransferObservationSchema84MigrationRetainsOriginalBytesAndCreatesNoAck()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; string original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { copy = await PrepareCompletedRetiredTransferAsync(first); original = await ReadTransferRetirementBytesAsync(fixture.Paths); }
        await ChangeTransferRetirementProjectionAsync(fixture.Paths, row =>
        { row["Intent"]!["TransferEntry"]!["Version"] = 2; row.AsObject().Remove("TransferSourceNameEpoch"); });
        original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE retired_transfer_observations; DROP INDEX namespace_transfer_source_items; DROP TRIGGER namespace_name_epoch_insert; DROP TRIGGER namespace_name_epoch_delete; DROP TRIGGER namespace_name_epoch_update; DROP TABLE namespace_name_epochs; PRAGMA user_version=84;");
        await using var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.HasCount(1, await migrated.ReadNamespaceTransferSourceGenerationsAsync(copy.Plan.Source.Identity.ItemId!.Value));
        Assert.IsEmpty(await migrated.ReadPendingRetiredTransferObservationsAsync(copy.Plan.SourceRoute.RootId));
        Assert.IsNull(await migrated.ReadRetiredTransferObservationAcknowledgementAsync(Guid.NewGuid()));
    }

    [TestMethod]
    [DataRow("worker")]
    [DataRow("mutation")]
    [DataRow("coalescing")]
    public async Task RetiredTransferObservationCannotReplaceAnExistingOrdinaryOwner(string owner)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        if (owner == "worker") await catalog.TryRecordWorkerRequestAsync(observation.OperationId, copy.Plan.SourceRoute.InstanceId, new byte[32]);
        else if (owner == "mutation") await catalog.PrepareMutationAsync(TransferJournalMutation(copy.Plan, observation.OperationId));
        else await catalog.PrepareJournalCoalescingAsync(TransferJournalCoalescing(copy.Plan, [observation.OperationId, Guid.NewGuid()]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(
            copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
    }

    [TestMethod]
    [DataRow("mutation")]
    [DataRow("coalescing-member")]
    [DataRow("coalescing-plan")]
    public async Task RetiredTransferObservationOwnershipCannotBeStolenByAnotherProductEffect(string owner)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        var started = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        if (owner == "mutation")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(TransferJournalMutation(copy.Plan, observation.OperationId)));
        else
        {
            var plan = TransferJournalCoalescing(copy.Plan, owner == "coalescing-member" ? [observation.OperationId, Guid.NewGuid()] : [Guid.NewGuid(), Guid.NewGuid()]);
            if (owner == "coalescing-plan") plan = plan with { PlanId = observation.OperationId };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(plan));
        }
        Assert.AreEqual(started, await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        Assert.IsNull(await catalog.ReadMutationAsync(observation.OperationId)); Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(observation.OperationId));
    }

    [TestMethod]
    public async Task RetiredTransferObservationKeepsAnEmptyOriginalCommandClaimEmpty()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog, empty: true); var observation = LateTransferObservation(copy);
        Assert.IsEmpty(copy.JournalClaim!.Members);
        Assert.AreEqual(MirrorPulseTransferAcknowledgementKind.CommandWithoutCapturedNotifications, copy.Acknowledgement!.Kind);
        var started = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        Assert.IsNotNull((await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(started, started.StartedAt.AddSeconds(1))).AcknowledgedAt);
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        Assert.IsEmpty((await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId))!.JournalClaim!.Members);
    }

    [TestMethod]
    public async Task RetiredTransferObservationLookupRetainsLegacyStartsWithoutAdoptingMissingProjection()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog);
        await ChangeTransferRetirementProjectionAsync(fixture.Paths, row =>
        {
            row["Intent"]!["TransferEntry"]!["Version"] = 1; row.AsObject().Remove("TransferSourceProjection");
            row.AsObject().Remove("TransferSourceNameEpoch");
        });
        var candidates = await catalog.ReadNamespaceTransferSourceGenerationsAsync(copy.Plan.Source.Identity.ItemId!.Value);
        Assert.HasCount(1, candidates); Assert.IsNull(candidates[0].Retirement.TransferSourceProjection);
        var observation = LateTransferObservation(copy);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(
            copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
    }

    [TestMethod]
    public async Task RetiredTransferObservationDoesNotInferAnAbsentNameAfterAnotherGenerationUsedIt()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        var retired = await catalog.ReadNamespaceRetirementAsync(copy.Plan.DeleteOperationId);
        Assert.IsNotNull(retired!.TransferSourceNameEpoch); Assert.IsGreaterThan(1L, retired.TransferSourceNameEpoch.Value);
        // Model a complete intervening placement/removal in the durable name ledger. Current
        // absence and the old SDK item do not identify which physical generation caused an event.
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_name_epochs SET epoch=epoch+2 WHERE parent_member_id='{copy.Plan.SourceParent.Context.Member.MemberId:D}' AND child_name_key='{copy.Plan.Source.Context.Location.RelativePath.Split('/')[^1].ToUpperInvariant()}';");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRetiredTransferObservationAcknowledgementAsync(
            copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId));
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
    }

    [TestMethod]
    public async Task RetiredTransferObservationAlreadyAcceptedMeaningDoesNotChangeWhenLaterNamesChange()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var observation = LateTransferObservation(copy);
        var started = await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, observation.ObservedAt.AddSeconds(1));
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_name_epochs SET epoch=epoch+2 WHERE parent_member_id='{copy.Plan.SourceParent.Context.Member.MemberId:D}' AND child_name_key='{copy.Plan.Source.Context.Location.RelativePath.Split('/')[^1].ToUpperInvariant()}';");
        Assert.AreEqual(started, await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation, started.StartedAt.AddSeconds(1)));
        var completed = await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(started, started.StartedAt.AddSeconds(2));
        Assert.AreEqual(started.SourceNameEpoch, completed.SourceNameEpoch);
        Assert.IsNotNull(completed.AcknowledgedAt);
    }

    private static async Task<MirrorPulseNamespaceTransferCopy> PrepareCompletedRetiredTransferAsync(MirrorPulseProductCatalog catalog, bool empty = false)
    {
        var target = await PrepareSettledTransferAsync(catalog, emptyJournal: empty); var started = await StartTransferAckAsync(catalog, target);
        return await catalog.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(1));
    }

    private static MirrorPulseTransferJournalObservation LateTransferObservation(MirrorPulseNamespaceTransferCopy copy) =>
        new(Guid.NewGuid(), copy.JournalClaim!.ThroughSequence + 1, MirrorPulseWorkerChangeKind.Delete, copy.Plan.Source.Identity.ItemId,
            copy.Plan.Source.Context.Location.RelativePath, null, false, copy.Acknowledgement!.AcknowledgedAt!.Value.AddSeconds(1));
}
