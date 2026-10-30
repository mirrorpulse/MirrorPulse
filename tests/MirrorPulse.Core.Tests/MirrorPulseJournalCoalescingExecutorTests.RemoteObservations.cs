using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task RemoteObservationRetainsErrorFactsAndSelectsTheEntireOriginalGroupAcrossCatalogOwners(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        MirrorPulseCoalescedConflictRemoteObservation selected; MirrorPulsePostRejectionRemoteVersion request;
        Guid[] previous; string rejectedBefore; string executionBefore;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
            rejectedBefore = JsonSerializer.Serialize(await catalog.ReadMutationAsync(request.RejectedEffect.OperationId));
            executionBefore = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            selected = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
            Assert.AreEqual(request.RemoteRevision, selected.RemoteRevision); Assert.IsNull(selected.OriginalErrorExpectedRevision);
            string? originalRequestBaseline = selected.RejectedEffect.ExpectedRevision;
            Assert.AreEqual(fixture.Revision, originalRequestBaseline);
            CollectionAssert.AreEqual(previous, selected.PreviousConflictIds.ToArray());
            CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), selected.OriginalOperationIds.ToArray());
            Assert.HasCount(previous.Length, await catalog.ReadUploadConflictsAsync());
            Assert.HasCount(previous.Length * 2, await catalog.ReadUploadConflictsAsync(pendingOnly: false));
            for (int index = 0; index < previous.Length; index++)
            {
                var old = (await catalog.ReadUploadConflictAsync(previous[index]))!;
                var current = (await catalog.ReadUploadConflictAsync(selected.SelectedConflictIds[index]))!;
                Assert.AreEqual(MirrorPulseConflictStatus.Dismissed, old.Status); Assert.IsNull(old.LocalRevision); Assert.IsNull(old.RemoteRevision);
                Assert.AreEqual(old.ChangeId, current.ChangeId); Assert.IsTrue(current.IsPending);
                Assert.AreEqual(fixture.Revision, current.LocalRevision); Assert.AreEqual(request.RemoteRevision, current.RemoteRevision);
            }
            Assert.AreEqual(rejectedBefore, JsonSerializer.Serialize(await catalog.ReadMutationAsync(request.RejectedEffect.OperationId)));
            Assert.AreEqual(executionBefore, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
            Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId);
        Assert.AreEqual(JsonSerializer.Serialize(selected), JsonSerializer.Serialize(retained));
        Assert.AreEqual(JsonSerializer.Serialize(selected), JsonSerializer.Serialize(await reopened.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request)));
        Assert.AreEqual(rejectedBefore, JsonSerializer.Serialize(await reopened.ReadMutationAsync(request.RejectedEffect.OperationId)));
        Assert.AreEqual(executionBefore, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("identity")]
    [DataRow("time")]
    [DataRow("effect")]
    [DataRow("item")]
    public async Task RemoteObservationReplayCannotReplaceASelectedVersionOrItsOriginalProvenance(string change)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var retained = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        string before = JsonSerializer.Serialize(retained);
        var changed = change switch
        {
            "version" => request with { RemoteRevision = "another-current-version" },
            "identity" => request with { ObservationId = Guid.NewGuid() },
            "time" => request with { ObservedAt = request.ObservedAt.AddTicks(1) },
            "effect" => request with { RejectedEffect = request.RejectedEffect with { ExpectedRevision = "invented-baseline" } },
            _ => request
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0],
            change == "item" ? Guid.NewGuid() : fixture.Plan.ItemId, fixture.Plan.FinalPath, changed));
        Assert.AreEqual(before, JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
        Assert.HasCount(previous.Length, await catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task RemoteObservationSqlFailureHalfwayThroughSelectionRollsBackEveryEpochAndItsOwnership()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Paths.ProductCatalogDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = "CREATE TRIGGER reject_second_observed_member BEFORE INSERT ON coalesced_conflict_remote_members WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'actual selection fault'); END;";
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request));
        Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId));
        CollectionAssert.AreEquivalent(previous, (await catalog.ReadUploadConflictsAsync()).Select(conflict => conflict.ConflictId).ToArray());
        Assert.HasCount(previous.Length, await catalog.ReadUploadConflictsAsync(pendingOnly: false));
        fault.CommandText = "DROP TRIGGER reject_second_observed_member;"; await fault.ExecuteNonQueryAsync();
        var completed = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Assert.AreEqual(request.ObservationId, completed.ObservationId);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("old-stat")]
    [DataRow("known-version")]
    public async Task RemoteObservationRefusesAnIncompleteOrChangedPendingGroup(string boundary)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        if (boundary == "missing") await catalog.SetUploadConflictStatusAsync(previous[^1], MirrorPulseConflictStatus.Dismissed);
        if (boundary == "duplicate")
            await catalog.SaveUploadConflictAsync(new(Guid.NewGuid(), fixture.Plan.InstanceId, fixture.Plan.Members[0].OperationId.ToString("D"), fixture.Plan.FinalPath,
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, null, null, DateTimeOffset.UtcNow));
        if (boundary == "old-stat") request = request with { StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        if (boundary == "known-version")
        {
            foreach (Guid id in previous) await catalog.SetUploadConflictStatusAsync(id, MirrorPulseConflictStatus.Dismissed);
            previous = await SaveGroupConflictsAsync(catalog, fixture, "already-selected");
        }
        string before = JsonSerializer.Serialize(await catalog.ReadUploadConflictsAsync(pendingOnly: false));
        Task Prepare() => catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        if (boundary is "missing" or "known-version") await Assert.ThrowsExactlyAsync<InvalidOperationException>(Prepare);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(Prepare);
        Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId));
        Assert.AreEqual(before, JsonSerializer.Serialize(await catalog.ReadUploadConflictsAsync(pendingOnly: false)));
    }

    [TestMethod]
    public async Task RemoteObservationFeedsTheExistingKeepBothChoiceWithoutChangingTheRejectedRequest()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var observation = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid selected = observation.SelectedConflictIds[0], command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, "keepboth", selected.ToString("D"), "pending"));
        var source = new MirrorPulseConflictRemoteSource(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey,
            observation.RemoteRelativePath, "selected-current-object", observation.RemoteRevision, SelectedRemoteBytes.Length);
        var decision = await catalog.PrepareConflictKeepBothAsync(command, selected, fixture.Plan.ItemId, fixture.Plan.FinalPath, source, null);
        Assert.AreEqual(request.RejectedEffect, decision.Intent.RejectedEffect);
        CollectionAssert.AreEqual(observation.SelectedConflictIds.ToArray(), decision.Intent.ConflictIds.ToArray());
        Assert.AreEqual(source, decision.Intent.RemoteSource); Assert.IsNull(decision.Copy); Assert.IsFalse(decision.RetryAuthorized);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
        foreach (var member in fixture.Plan.Members) Assert.AreEqual(command, await catalog.ReadConflictKeepBothOwnerAsync(member.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(observation), JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("observation-table")]
    [DataRow("member-table")]
    [DataRow("head-table")]
    [DataRow("downgraded-version")]
    public async Task RemoteObservationSchemaRefusesLostOrDowngradedOwnershipInsteadOfInventingAnEpoch(string boundary)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
            await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        }
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Paths.ProductCatalogDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = false
        };
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync(); await using var change = connection.CreateCommand();
            change.CommandText = boundary switch
            {
                "observation-table" => "DROP TABLE coalesced_conflict_remote_observations;",
                "member-table" => "DROP TABLE coalesced_conflict_remote_members;",
                "head-table" => "DROP TABLE coalesced_conflict_remote_heads;",
                _ => "PRAGMA user_version=92;"
            };
            await change.ExecuteNonQueryAsync();
        }
        MirrorPulseProductCatalog? unexpected = null;
        try { await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => unexpected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)); }
        finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
        await using var inspected = new SqliteConnection(options.ToString()); await inspected.OpenAsync(); await using var inspect = inspected.CreateCommand();
        inspect.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(boundary == "downgraded-version" ? 92L : 93L, await inspect.ExecuteScalarAsync());
        inspect.CommandText = "SELECT COUNT(*) FROM upload_conflicts;";
        Assert.AreEqual(fixture.Plan.Members.Count * 2L, await inspect.ExecuteScalarAsync());
        if (boundary != "downgraded-version")
        {
            inspect.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            inspect.Parameters.AddWithValue("$name", boundary switch
            {
                "observation-table" => "coalesced_conflict_remote_observations",
                "head-table" => "coalesced_conflict_remote_heads",
                _ => "coalesced_conflict_remote_members"
            });
            Assert.AreEqual(0L, await inspect.ExecuteScalarAsync());
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task<(Guid[] Previous, MirrorPulsePostRejectionRemoteVersion Observation)> RejectWithoutVersionMetadataAsync(
        MirrorPulseProductCatalog catalog, Fixture fixture, FileRemote remote)
    {
        var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(execution.Steps[0].PreviousRelativePath ?? execution.Steps[0].RelativePath, "remote-at-rejection", SelectedRemoteBytes);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var rejected = await catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State);
        var previous = new List<Guid>();
        foreach (var member in fixture.Plan.Members)
        {
            Guid id = Guid.NewGuid(); previous.Add(id);
            await catalog.SaveUploadConflictAsync(new(id, fixture.Plan.InstanceId, member.OperationId.ToString("D"), fixture.Plan.FinalPath,
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, null, null, DateTimeOffset.UtcNow));
        }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        return (previous.ToArray(), new(1, Guid.NewGuid(), rejected.Intent, "selected-by-later-stat", started, DateTimeOffset.UtcNow));
    }
}
