using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OriginalTransferAckAtomicallyCompletesHistoryAndReleasesOnlyItsTwoRootsAcrossOwners(bool sameInstance, bool emptyJournal)
    {
        using var fixture = new Fixture(); TransferPublicationFixture target; MirrorPulseNamespaceTransferCopy started;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareSettledTransferAsync(first, sameInstance, emptyJournal);
            started = await StartTransferAckAsync(first, target);
            Assert.IsNull(started.Acknowledgement!.AcknowledgedAt);
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
            Assert.HasCount(1, await first.ReadPendingNamespaceTransferStatusesAsync(target.Copy.Plan.SourceRoute.RootId));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(started, await second.ReadNamespaceTransferAsync(started.Plan.OperationId));
            var completed = await second.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(1));
            Assert.HasCount(0, await second.ReadNamespaceDispatchFencedRootsAsync());
            Assert.HasCount(0, await second.ReadPendingNamespaceTransferStatusesAsync(started.Plan.SourceRoute.RootId));
            Assert.HasCount(0, await second.ReadPendingNamespaceTransferStatusesAsync(started.Plan.TargetRoute.RootId));
            Assert.AreEqual(started.Plan, completed.Plan); Assert.AreEqual(started.JournalClaim, completed.JournalClaim);
            foreach (var original in completed.JournalClaim!.Members)
            {
                Assert.AreEqual(completed.Plan.OperationId, await second.ReadNamespaceTransferJournalOwnerAsync(original.OperationId));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareMutationAsync(TransferJournalMutation(completed.Plan, original.OperationId)));
            }
            var status = (await second.ReadNamespaceTransferStatusAsync(started.Plan.OperationId))!;
            Assert.IsTrue(status.Completed); Assert.IsFalse(status.RootsFenced); Assert.AreEqual(!emptyJournal, status.JournalAcknowledged);
            Assert.AreEqual(emptyJournal ? "CompletedCommandWithoutCapturedNotifications" : "Completed", status.Stage);
            Assert.AreEqual(completed, await second.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, DateTimeOffset.MinValue));
        }
        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var topology = await third.ReadAdapterTopologyAsync();
        await third.SaveAdapterTopologyAsync(topology with { Instances = topology.Instances.Select(instance => SettlementInstance(instance, disabled: true)).ToArray() });
        var history = (await third.ReadNamespaceTransferAsync(started.Plan.OperationId))!;
        Assert.IsNotNull(history.Acknowledgement!.AcknowledgedAt);
        Assert.AreEqual(history, await third.StartNamespaceTransferAcknowledgementAsync(history.Plan.OperationId,
            started.Acknowledgement.TargetAtStart, started.Acknowledgement.TargetProtectionId, DateTimeOffset.MinValue));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("partition")]
    [DataRow("first-root")]
    [DataRow("second-root")]
    public async Task OriginalTransferAckReceiptAndTwoRootReleaseRollbackTogetherAtEverySqlWrite(string write)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog); var started = await StartTransferAckAsync(catalog, target);
        string sql = write switch
        {
            "payload" => "CREATE TRIGGER fail_ack BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'ack-payload'); END;",
            "partition" => "CREATE TRIGGER fail_ack BEFORE UPDATE ON namespace_transfer_states BEGIN SELECT RAISE(ABORT,'ack-state'); END;",
            _ => $"CREATE TRIGGER fail_ack BEFORE DELETE ON namespace_transfer_roots WHEN OLD.root_id='{(write == "first-root" ? started.Plan.SourceRoute.RootId : started.Plan.TargetRoute.RootId)}' BEGIN SELECT RAISE(ABORT,'ack-root'); END;"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(1)));
        Assert.AreEqual(started, await catalog.ReadNamespaceTransferAsync(started.Plan.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_ack;");
        Assert.IsNotNull((await catalog.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(2))).Acknowledgement!.AcknowledgedAt);
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("foreign-start")]
    [DataRow("rewritten-proof")]
    [DataRow("rewritten-kind")]
    [DataRow("before-start")]
    [DataRow("cancelled")]
    public async Task TransferAckCannotCompleteUsingAnotherStartOrCancelAwayTheOwnedFences(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog); var copy = await StartTransferAckAsync(catalog, target); var start = copy.Acknowledgement!;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var bad = change switch
        {
            "foreign-start" => start with { TargetProtectionId = Guid.NewGuid() },
            "rewritten-proof" => start with { TargetAtStart = start.TargetAtStart with { VerifiedAt = start.TargetAtStart.VerifiedAt.AddTicks(1) } },
            "rewritten-kind" => start with { Kind = MirrorPulseTransferAcknowledgementKind.CommandWithoutCapturedNotifications },
            _ => start
        };
        if (change == "cancelled")
            await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.CompleteNamespaceTransferAcknowledgementAsync(bad, start.StartedAt.AddSeconds(1), cancelled.Token));
        else if (change == "before-start")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.CompleteNamespaceTransferAcknowledgementAsync(bad, start.StartedAt.AddTicks(-1)));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceTransferAcknowledgementAsync(bad, start.StartedAt.AddSeconds(1)));
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("not-retired")]
    [DataRow("legacy")]
    [DataRow("disabled")]
    [DataRow("changed-route")]
    [DataRow("wrong-target-epoch")]
    [DataRow("stale-target-proof")]
    public async Task OriginalTransferAckRequiresItsCommandCapturedSetSettledSourceAndCurrentRoute(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog, stopBeforeRetirement: change == "not-retired", legacy: change == "legacy");
        if (change is "disabled" or "changed-route")
        {
            var topology = await catalog.ReadAdapterTopologyAsync();
            await catalog.SaveAdapterTopologyAsync(topology with
            {
                Instances = topology.Instances.Select(instance =>
                SettlementInstance(instance, disabled: change == "disabled", changed: change == "changed-route")).ToArray()
            });
        }
        var proof = SettlementTarget(target);
        if (change == "stale-target-proof") proof = proof with { VerifiedAt = target.Publication.PublishedAt };
        Task StartAsync() => catalog.StartNamespaceTransferAcknowledgementAsync(target.Copy.Plan.OperationId, proof,
            change == "wrong-target-epoch" ? Guid.NewGuid() : target.Protection.ProtectionId, proof.VerifiedAt.AddSeconds(1));
        if (change is "legacy" or "stale-target-proof") await Assert.ThrowsExactlyAsync<ArgumentException>(StartAsync);
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(StartAsync);
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.Acknowledgement);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("orphan")]
    [DataRow("false-completion")]
    [DataRow("fingerprint")]
    [DataRow("missing-journal")]
    public async Task PendingTransferDiscoveryDoesNotHideMissingOrContradictoryCompletionPartitions(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog);
        string sql = damage switch
        {
            "missing" => "DELETE FROM namespace_transfer_states;",
            "orphan" => $"PRAGMA foreign_keys=OFF; INSERT INTO namespace_transfer_states VALUES('{Guid.NewGuid():D}',1,zeroblob(32));",
            "false-completion" => "UPDATE namespace_transfer_states SET completed=1;",
            "fingerprint" => "UPDATE namespace_transfer_states SET payload_fingerprint=zeroblob(32);",
            _ => "DELETE FROM namespace_transfer_journal_members;"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(target.Copy.Plan.SourceRoute.RootId));
    }

    [TestMethod]
    public async Task TransferSchema82MigrationRetainsOriginalPayloadAndBothPendingFencesAndNeverRecreatesStamped83History()
    {
        using var fixture = new Fixture(); Guid operation; string payload;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var target = await PrepareSettledTransferAsync(first); operation = target.Copy.Plan.OperationId;
            payload = await ReadTransferPayloadHexAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_states; PRAGMA user_version=82;");
        await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(payload, await ReadTransferPayloadHexAsync(fixture.Paths));
            Assert.IsNull((await migrated.ReadNamespaceTransferAsync(operation))!.Acknowledgement);
            Assert.HasCount(2, await migrated.ReadNamespaceDispatchFencedRootsAsync());
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_states;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        Assert.AreEqual(payload, await ReadTransferPayloadHexAsync(fixture.Paths));
    }

    private static async Task<TransferPublicationFixture> PrepareSettledTransferAsync(MirrorPulseProductCatalog catalog,
        bool sameInstance = true, bool emptyJournal = false, bool stopBeforeRetirement = false, bool legacy = false,
        MirrorPulseNamespaceTransferPlan? originalPlan = null)
    {
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance, captureOriginalJournal: !legacy, emptyJournal: emptyJournal, originalPlan: originalPlan);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var sourceProof = FreshSourceDeleteTarget(target);
        await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, sourceProof, target.Protection.ProtectionId, sourceProof.VerifiedAt);
        await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(target.Copy.Plan.OperationId, null, sourceProof.VerifiedAt.AddSeconds(1));
        if (!stopBeforeRetirement)
        {
            var proof = FreshTransferRetirementTarget(target);
            await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
            await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, proof.VerifiedAt.AddSeconds(1)), proof.VerifiedAt.AddSeconds(1));
            await catalog.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, TransferRetirementReceipt(target, proof.VerifiedAt.AddSeconds(2)));
            await catalog.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof.VerifiedAt.AddSeconds(3));
        }
        return target;
    }

    private static MirrorPulseTransferTargetVerification SettlementTarget(TransferPublicationFixture target) =>
        target.Copy.TargetVerification! with { VerifiedAt = target.Publication.PublishedAt.AddSeconds(8) };

    private static Task<MirrorPulseNamespaceTransferCopy> StartTransferAckAsync(MirrorPulseProductCatalog catalog, TransferPublicationFixture target) =>
        catalog.StartNamespaceTransferAcknowledgementAsync(target.Copy.Plan.OperationId, SettlementTarget(target), target.Protection.ProtectionId,
            target.Publication.PublishedAt.AddSeconds(9));

    private static AdapterInstance SettlementInstance(AdapterInstance instance, bool disabled, bool changed = false) => new(instance.AdapterId,
        instance.InstallId, instance.InstanceId, instance.DisplayName, changed ? new Dictionary<string, string> { ["endpoint"] = "changed" } :
            instance.Configuration, instance.CredentialReferences, instance.FileCacheDirectory, instance.TransferCacheDirectory,
        !disabled && instance.Enabled, instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt);
}
