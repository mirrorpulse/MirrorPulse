using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    public async Task RemoteObservationRefreshAppendsHistoryAndReplaysAnEarlierCommandWithoutRetargetingTheCurrentHead(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        MirrorPulseCoalescedConflictRemoteObservation first, second, third; MirrorPulsePostRejectionRemoteVersion secondInput;
        Guid command; string originalEffect, originalProgram;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
            originalEffect = JsonSerializer.Serialize(await catalog.ReadMutationAsync(request.RejectedEffect.OperationId));
            originalProgram = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            first = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
            var target = await catalog.ReadCoalescedConflictRemoteTargetAsync(first.SelectedConflictIds[0]);
            Assert.AreEqual(first.ObservationId, target.CurrentObservationId); Assert.AreEqual(request.RejectedEffect, target.RejectedEffect);
            Assert.AreEqual(fixture.Plan.FinalPath, target.LocalRootRelativePath); Assert.AreEqual(first.RemoteRelativePath, target.RemoteRelativePath);
            command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "refreshconflict", first.SelectedConflictIds[0].ToString("D"), "pending"));
            secondInput = LaterObservation(request, "current-after-another-remote-change");
            second = await catalog.RefreshCoalescedConflictRemoteObservationAsync(command, first.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, secondInput);
            Assert.AreEqual(2, second.Version); Assert.AreEqual(first.ObservationId, second.PreviousObservationId);
            Assert.IsNull(second.OriginalErrorExpectedRevision); Assert.IsNull(second.OriginalErrorActualRevision);
            CollectionAssert.AreEqual(previous, second.InitialConflictIds!.ToArray());
            CollectionAssert.AreEqual(first.SelectedConflictIds.ToArray(), second.PreviousConflictIds.ToArray());
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(command))!.State);
            Guid laterCommand = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(laterCommand, "refreshconflict", second.SelectedConflictIds[0].ToString("D"), "pending"));
            third = await catalog.RefreshCoalescedConflictRemoteObservationAsync(laterCommand, second.SelectedConflictIds[0], fixture.Plan.ItemId,
                fixture.Plan.FinalPath, LaterObservation(request, "current-after-a-third-remote-change"));
            Assert.AreEqual(second.ObservationId, third.PreviousObservationId); Assert.IsNull(third.OriginalErrorExpectedRevision);
            foreach (Guid id in first.SelectedConflictIds.Concat(second.SelectedConflictIds))
                Assert.AreEqual(MirrorPulseConflictStatus.Dismissed, (await catalog.ReadUploadConflictAsync(id))!.Status);
            CollectionAssert.AreEquivalent(third.SelectedConflictIds.ToArray(), (await catalog.ReadUploadConflictsAsync()).Select(row => row.ConflictId).ToArray());
            Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        foreach (var historical in new[] { first, second, third })
            Assert.AreEqual(JsonSerializer.Serialize(historical), JsonSerializer.Serialize(await reopened.ReadCoalescedConflictRemoteObservationEpochAsync(historical.ObservationId)));
        var replay = await reopened.RefreshCoalescedConflictRemoteObservationAsync(command, first.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, secondInput);
        Assert.AreEqual(JsonSerializer.Serialize(second), JsonSerializer.Serialize(replay));
        Assert.AreEqual(JsonSerializer.Serialize(second), JsonSerializer.Serialize(await reopened.ReadCoalescedConflictRemoteRefreshAsync(command)));
        Assert.AreEqual(JsonSerializer.Serialize(third), JsonSerializer.Serialize(await reopened.ReadCoalescedConflictRemoteObservationAsync(first.RejectedEffect.OperationId)));
        Assert.AreEqual(originalEffect, JsonSerializer.Serialize(await reopened.ReadMutationAsync(first.RejectedEffect.OperationId)));
        Assert.AreEqual(originalProgram, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadCoalescedConflictRemoteTargetAsync(first.SelectedConflictIds[0]));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("worker-reported-version")]
    [DataRow("")]
    public async Task RemoteObservationRefreshPreservesKnownOrEmptyErrorMetadataInsteadOfReplacingItsHistoricalMeaning(string reported)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (unknown, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        foreach (Guid id in unknown) await catalog.SetUploadConflictStatusAsync(id, MirrorPulseConflictStatus.Dismissed);
        Guid[] original = await SaveGroupConflictsAsync(catalog, fixture, reported); Guid command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, "refreshconflict", original[0].ToString("D"), "pending"));
        var observation = await catalog.RefreshCoalescedConflictRemoteObservationAsync(command, original[0], fixture.Plan.ItemId,
            fixture.Plan.FinalPath, LaterObservation(request, "independently-observed-current"));
        Assert.AreEqual(1, observation.Version); Assert.AreEqual(reported, observation.OriginalErrorActualRevision);
        Assert.AreEqual(fixture.Revision, observation.OriginalErrorExpectedRevision);
        foreach (Guid id in original) Assert.AreEqual(reported, (await catalog.ReadUploadConflictAsync(id))!.RemoteRevision);
        Assert.AreEqual(JsonSerializer.Serialize(observation), JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("target")]
    [DataRow("state")]
    [DataRow("owned")]
    public async Task RemoteObservationRefreshRequiresTheActualUserCommandAndCannotStealAnOwnedChoice(string invalid)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var first = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid(), selected = first.SelectedConflictIds[0];
        await catalog.SaveUserCommandAsync(new(command, invalid == "action" ? "keepboth" : "refreshconflict",
            invalid == "target" ? Guid.NewGuid().ToString("D") : selected.ToString("D"), invalid == "state" ? "resolved" : "pending"));
        if (invalid == "owned")
        {
            Guid owner = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(owner, "keepboth", selected.ToString("D"), "pending"));
            await catalog.PrepareConflictKeepBothAsync(owner, selected, fixture.Plan.ItemId, fixture.Plan.FinalPath,
                new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, first.RemoteRelativePath, "selected-object", first.RemoteRevision, SelectedRemoteBytes.Length), null);
        }
        var next = LaterObservation(request, "must-not-replace-an-owned-selection");
        Task Refresh() => catalog.RefreshCoalescedConflictRemoteObservationAsync(command, selected, fixture.Plan.ItemId, fixture.Plan.FinalPath, next);
        if (invalid == "owned") await Assert.ThrowsExactlyAsync<InvalidOperationException>(Refresh);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(Refresh);
        Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationEpochAsync(next.ObservationId));
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
    }

    [TestMethod]
    public async Task RemoteObservationRefreshHeadWriteFailureRollsBackNewEpochsAndCommandCompletion()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var first = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "refreshconflict", first.SelectedConflictIds[0].ToString("D"), "pending"));
        var next = LaterObservation(request, "a-real-current-version-observation");
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = "CREATE TRIGGER reject_observation_head BEFORE UPDATE ON coalesced_conflict_remote_heads BEGIN SELECT RAISE(ABORT,'actual head fault'); END;";
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RefreshCoalescedConflictRemoteObservationAsync(command, first.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, next));
        Assert.IsNull(await catalog.ReadCoalescedConflictRemoteRefreshAsync(command)); Assert.IsNull(await catalog.ReadCoalescedConflictRemoteObservationEpochAsync(next.ObservationId));
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))!.State);
        CollectionAssert.AreEquivalent(first.SelectedConflictIds.ToArray(), (await catalog.ReadUploadConflictsAsync()).Select(row => row.ConflictId).ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId)));
        fault.CommandText = "DROP TRIGGER reject_observation_head;"; await fault.ExecuteNonQueryAsync();
        var refreshed = await catalog.RefreshCoalescedConflictRemoteObservationAsync(command, first.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, next);
        Assert.AreEqual(next.ObservationId, refreshed.ObservationId);
    }

    [TestMethod]
    [DataRow("keepboth", "head")]
    [DataRow("retry", "head")]
    [DataRow("keeplocal", "head")]
    [DataRow("keepboth", "all-members")]
    [DataRow("retry", "all-members")]
    [DataRow("keeplocal", "all-members")]
    [DataRow("keepboth", "one-member")]
    [DataRow("retry", "one-member")]
    [DataRow("keeplocal", "one-member")]
    public async Task RemoteObservationLostCurrentOwnershipCannotGrantKeepBothOrRetryAuthority(string action, string lost)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var first = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid(), selected = first.SelectedConflictIds[0];
        await catalog.SaveUserCommandAsync(new(command, action, selected.ToString("D"), "pending"));
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = lost switch
        {
            "head" => "DELETE FROM coalesced_conflict_remote_heads;",
            "all-members" => "DELETE FROM coalesced_conflict_remote_members;",
            _ => "DELETE FROM coalesced_conflict_remote_members WHERE ordinal=0;"
        };
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadCoalescedConflictRemoteObservationAsync(request.RejectedEffect.OperationId));
        if (action == "keepboth")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareConflictKeepBothAsync(command, selected, fixture.Plan.ItemId, fixture.Plan.FinalPath,
                new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, first.RemoteRelativePath, "selected-object", first.RemoteRevision, SelectedRemoteBytes.Length), null));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.AuthorizeJournalCoalescingRetryAsync(command, selected, action == "keeplocal"));
        Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
    }

    private static MirrorPulsePostRejectionRemoteVersion LaterObservation(MirrorPulsePostRejectionRemoteVersion original, string revision)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return original with { ObservationId = Guid.NewGuid(), RemoteRevision = revision, StartedAt = now, ObservedAt = now };
    }

    private static SqliteConnection ObservationFaultConnection(Fixture fixture) => new(new SqliteConnectionStringBuilder
    {
        DataSource = fixture.Paths.ProductCatalogDatabasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        ForeignKeys = false
    }.ToString());
}
