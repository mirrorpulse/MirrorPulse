using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    public async Task RemoteRefreshCommandPreparationDoesNotResetALostCompletedReturnOrRetargetAnEarlierChoice(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        Guid command = Guid.NewGuid(), entry; MirrorPulseCoalescedConflictRemoteObservation first, second; string original, execution;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (previous, input) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote); entry = previous[0];
            original = JsonSerializer.Serialize(await catalog.ReadMutationAsync(input.RejectedEffect.OperationId));
            execution = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            var pending = await catalog.BeginCoalescedConflictRemoteRefreshAsync(command, entry);
            Assert.AreEqual("pending", pending.State); Assert.AreEqual("refreshconflict", pending.Action); Assert.AreEqual(entry.ToString("D"), pending.TargetId);
            Assert.AreEqual(pending, await catalog.BeginCoalescedConflictRemoteRefreshAsync(command, entry));
            Assert.IsNull(await catalog.ReadCoalescedConflictRemoteRefreshAsync(command)); Assert.HasCount(previous.Length, await catalog.ReadUploadConflictsAsync());
            Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
            first = await catalog.RefreshCoalescedConflictRemoteObservationAsync(command, entry, fixture.Plan.ItemId, fixture.Plan.FinalPath, input);
            Guid next = Guid.NewGuid();
            await catalog.BeginCoalescedConflictRemoteRefreshAsync(next, first.SelectedConflictIds[0]);
            second = await catalog.RefreshCoalescedConflictRemoteObservationAsync(next, first.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath,
                LaterObservation(input, "later-current-selected-version"));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = await reopened.BeginCoalescedConflictRemoteRefreshAsync(command, entry);
        Assert.AreEqual("resolved", replay.State); Assert.AreEqual(entry.ToString("D"), replay.TargetId);
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await reopened.ReadCoalescedConflictRemoteRefreshAsync(command)));
        Assert.AreEqual(JsonSerializer.Serialize(second), JsonSerializer.Serialize(await reopened.ReadCoalescedConflictRemoteObservationAsync(first.RejectedEffect.OperationId)));
        Assert.AreEqual(original, JsonSerializer.Serialize(await reopened.ReadMutationAsync(first.RejectedEffect.OperationId)));
        Assert.AreEqual(execution, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.IsEmpty(await reopened.ReadPendingJournalCoalescingRetryGroupsAsync()); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.BeginCoalescedConflictRemoteRefreshAsync(command, second.SelectedConflictIds[0]));
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("target")]
    [DataRow("resolved-without-observation")]
    public async Task RemoteRefreshCommandPreparationRefusesAnotherIntentAndNeverManufacturesACompletedObservation(string invalid)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote); Guid command = Guid.NewGuid();
        var retained = new MirrorPulseUserCommandRecord(command, invalid == "action" ? "keepboth" : "refreshconflict",
            invalid == "target" ? Guid.NewGuid().ToString("D") : previous[0].ToString("D"), invalid == "resolved-without-observation" ? "resolved" : "pending");
        await catalog.SaveUserCommandAsync(retained);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(command, previous[0]));
        Assert.AreEqual(retained, await catalog.ReadUserCommandAsync(command));
        Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId));
        Assert.HasCount(previous.Length, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
    }

    [TestMethod]
    public async Task RemoteRefreshCommandPreparationCannotAdoptAnOwnedPreservationOrInsertItsCommand()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var observation = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid owner = Guid.NewGuid(), command = Guid.NewGuid(), selected = observation.SelectedConflictIds[0];
        await catalog.SaveUserCommandAsync(new(owner, "keepboth", selected.ToString("D"), "pending"));
        await catalog.PrepareConflictKeepBothAsync(owner, selected, fixture.Plan.ItemId, fixture.Plan.FinalPath,
            new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, observation.RemoteRelativePath, "selected-object", observation.RemoteRevision, SelectedRemoteBytes.Length), null);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(command, selected));
        Assert.IsNull(await catalog.ReadUserCommandAsync(command));
        Assert.AreEqual(JsonSerializer.Serialize(observation), JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
        foreach (Guid original in observation.OriginalOperationIds) Assert.AreEqual(owner, await catalog.ReadConflictKeepBothOwnerAsync(original));
    }

    [TestMethod]
    public async Task RemoteRefreshCommandPreparationKeepsAnUnobservedPendingCommandBoundToItsOriginalEpoch()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote); Guid command = Guid.NewGuid(), peer = Guid.NewGuid();
        var retained = await catalog.BeginCoalescedConflictRemoteRefreshAsync(command, previous[0]);
        await catalog.BeginCoalescedConflictRemoteRefreshAsync(peer, previous[0]);
        var observed = await catalog.RefreshCoalescedConflictRemoteObservationAsync(peer, previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(command, previous[0]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(command, observed.SelectedConflictIds[0]));
        Assert.AreEqual(retained, await catalog.ReadUserCommandAsync(command)); Assert.IsNull(await catalog.ReadCoalescedConflictRemoteRefreshAsync(command));
    }

    [TestMethod]
    public async Task RemoteRefreshCommandInsertFailureRollsBackWithoutChangingOriginalEpochsOrAuthorizingAChoice()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote); Guid command = Guid.NewGuid();
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = "CREATE TRIGGER reject_refresh_command BEFORE INSERT ON user_commands WHEN NEW.action='refreshconflict' BEGIN SELECT RAISE(ABORT,'actual command insert fault'); END;";
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(command, previous[0]));
        Assert.IsNull(await catalog.ReadUserCommandAsync(command)); Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId));
        CollectionAssert.AreEquivalent(previous, (await catalog.ReadUploadConflictsAsync()).Select(row => row.ConflictId).ToArray());
        Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        fault.CommandText = "DROP TRIGGER reject_refresh_command;"; await fault.ExecuteNonQueryAsync();
        Assert.AreEqual("pending", (await catalog.BeginCoalescedConflictRemoteRefreshAsync(command, previous[0])).State);
    }
}
