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
    public async Task ExplicitRetryUsesANewEffectAndPreservesRejectedHistoryForEveryProgram(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        MirrorPulseMutationRecord rejected; MirrorPulseJournalCoalescingRetry retry;
        string executionBefore; string conflictingPath;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
            conflictingPath = prepared.Steps[0].PreviousRelativePath ?? prepared.Steps[0].RelativePath;
            remote.Seed(conflictingPath, "concurrent-remote", [7, 7, 7]);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            var execution = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
            executionBefore = JsonSerializer.Serialize(execution);
            rejected = (await catalog.ReadMutationAsync(execution.Steps[0].OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.Conflict, rejected.State);
            var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "concurrent-remote");
            Guid command = Guid.NewGuid();
            await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[1].ToString("D"), "pending"));
            retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[1], false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId));
            Assert.Contains(fixture.Plan.PlanId, (await catalog.ReadPendingJournalCoalescingRetryGroupsAsync()).ToArray());
            string? expectedRevision = fixture.Revision;
            Assert.AreEqual(expectedRevision, retry.Intent.ExpectedRevision);
            Assert.AreEqual(rejected, await catalog.ReadMutationAsync(rejected.Intent.OperationId));
            Assert.AreNotEqual(rejected.Intent.OperationId, retry.Intent.OperationId);
            Assert.AreEqual(JsonSerializer.Serialize(retry), JsonSerializer.Serialize(await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[1], false)));
            foreach (var member in fixture.Plan.Members) Assert.IsFalse(await catalog.HasPendingUploadConflictAsync(member.OperationId));
        }
        // A new source observation restores the expected precondition. Retry
        // still uses the original baseline, not the previously conflicting one.
        File.Delete(Path.Combine(fixture.Paths.DataRootPath, "synthetic-remote", conflictingPath));
        remote = new FileRemote(fixture);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var results = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.AreEqual(retry.Intent.OperationId, results[0].Intent.OperationId);
        Assert.IsTrue(results.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual(rejected, await reopened.ReadMutationAsync(rejected.Intent.OperationId));
        Assert.AreEqual(executionBefore, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        await reopened.CompleteJournalCoalescingRetryCommandsAsync(fixture.Plan.PlanId);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(retry.CommandId))!.State);
        Assert.IsEmpty(await reopened.ReadPendingJournalCoalescingRetryGroupsAsync());
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    public async Task KeepLocalAuthorizesObservedBaselineWithoutRewritingTheOriginalProgram(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(prepared.Steps[0].PreviousRelativePath ?? prepared.Steps[0].RelativePath, "observed-remote", [5, 5, 5]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "observed-remote");
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "keeplocal", conflicts[0].ToString("D"), "pending"));
        var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], true);
        Assert.AreEqual("observed-remote", retry.Intent.ExpectedRevision);
        var completed = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        string? expectedRevision = fixture.Revision;
        Assert.AreEqual(expectedRevision, (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!.ExpectedRevision);
    }

    [TestMethod]
    public async Task RepeatedRejectionsHaveDistinctConflictAndRetryEpochs()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var ids = new HashSet<Guid>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            remote.Seed(fixture.Plan.OriginalPath, "remote-" + attempt, [6, 6, 6]);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "remote-" + attempt);
            Assert.IsTrue(await catalog.HasPendingUploadConflictAsync(fixture.Plan.Members[0].OperationId));
            Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "retry", conflicts[0].ToString("D"), "pending"));
            var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false);
            Assert.IsTrue(ids.Add(retry.Intent.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(retry.PreviousOperationId))!.State);
        }
        Assert.HasCount(3 * fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync(pendingOnly: false));
        Assert.IsEmpty(await catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    [DataRow("no-command")]
    [DataRow("wrong-action")]
    [DataRow("wrong-target")]
    [DataRow("incomplete-group")]
    [DataRow("ambiguous-effect")]
    public async Task InvalidAuthorizationLeavesAllOriginalConflictsAndEffectHistoryUnchanged(string scenario)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        if (scenario == "ambiguous-effect")
        {
            remote.LoseReplyAfter = "Upload";
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        }
        else
        {
            remote.Seed(fixture.Plan.OriginalPath, "changed", [8]);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        }
        var conflicts = await SaveGroupConflictsAsync(catalog, fixture, "changed");
        if (scenario == "incomplete-group") await catalog.SetUploadConflictStatusAsync(conflicts[1], MirrorPulseConflictStatus.Resolved);
        var history = (await catalog.ReadIncompleteMutationsAsync()).ToArray();
        Guid command = Guid.NewGuid();
        if (scenario != "no-command") await catalog.SaveUserCommandAsync(new(command, scenario == "wrong-action" ? "delete" : "retry",
            (scenario == "wrong-target" ? Guid.NewGuid() : conflicts[0]).ToString("D"), "pending"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeJournalCoalescingRetryAsync(command, conflicts[0], false));
        CollectionAssert.AreEqual(history, (await catalog.ReadIncompleteMutationsAsync()).ToArray());
        Assert.IsTrue(await catalog.HasPendingUploadConflictAsync(fixture.Plan.Members[0].OperationId));
    }

    private static async Task<Guid[]> SaveGroupConflictsAsync(MirrorPulseProductCatalog catalog, Fixture fixture, string observed)
    {
        var conflicts = new List<Guid>();
        foreach (var member in fixture.Plan.Members)
        {
            Guid conflict = Guid.NewGuid(); conflicts.Add(conflict);
            await catalog.SaveUploadConflictAsync(new(conflict, fixture.Plan.InstanceId, member.OperationId.ToString("D"), fixture.Plan.FinalPath,
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, fixture.Revision, observed, DateTimeOffset.UtcNow));
        }
        return conflicts.ToArray();
    }

    [TestMethod]
    public async Task Schema54UpgradePreservesRejectedExecutionAndDoesNotInventRetryAuthorization()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        MirrorPulseMutationRecord rejected; Guid selected; Guid command = Guid.NewGuid(); string originalExecution;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
            originalExecution = JsonSerializer.Serialize(execution);
            remote.Seed(fixture.Plan.OriginalPath, "changed", [2]);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            rejected = (await catalog.ReadMutationAsync(execution.Steps[0].OperationId))!;
            selected = (await SaveGroupConflictsAsync(catalog, fixture, "changed"))[0];
            await catalog.SaveUserCommandAsync(new(command, "retry", selected.ToString("D"), "pending"));
        }
        await ExecuteRetrySqlAsync(fixture, "DROP TABLE journal_coalescing_retries; PRAGMA user_version=54;");
        await using var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(rejected, await upgraded.ReadMutationAsync(rejected.Intent.OperationId));
        Assert.AreEqual(originalExecution, JsonSerializer.Serialize(await upgraded.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.IsEmpty(await upgraded.ReadPendingJournalCoalescingRetryGroupsAsync());
        var authorized = await upgraded.AuthorizeJournalCoalescingRetryAsync(command, selected, false);
        Assert.AreEqual(rejected.Intent.OperationId, authorized.PreviousOperationId);
        Assert.AreEqual(MirrorPulseMutationState.Prepared, (await upgraded.ReadMutationAsync(authorized.Intent.OperationId))!.State);
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("authorization")]
    public async Task CorruptRetryProofCannotAuthorizeAnEffect(string corruption)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        remote.Seed(fixture.Plan.OriginalPath, "changed", [1]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var conflict = (await SaveGroupConflictsAsync(catalog, fixture, "changed"))[0]; Guid command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, "retry", conflict.ToString("D"), "pending"));
        var retry = await catalog.AuthorizeJournalCoalescingRetryAsync(command, conflict, false);
        await ExecuteRetrySqlAsync(fixture, corruption == "fingerprint" ?
            "UPDATE journal_coalescing_retries SET fingerprint=zeroblob(32);" : "UPDATE user_commands SET target_id='00000000-0000-0000-0000-000000000001';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0));
        Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(retry.Intent.OperationId))!.State);
    }

    private static async Task ExecuteRetrySqlAsync(Fixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var query = connection.CreateCommand(); query.CommandText = sql;
        await query.ExecuteNonQueryAsync();
    }
}
