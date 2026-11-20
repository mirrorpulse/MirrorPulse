using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingAtomicPreparationTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task ExplicitTransferCommitsOriginalHistoryAndCompleteExecutionTogetherForEveryProgram(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect);
        var original = fixture.Intent(fixture.Window[0]);
        MirrorPulseJournalCoalescingExecution execution;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareMutationAsync(original);
            execution = await catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(fixture.Plan,
                fixture.Window, [original], fixture.Revision, fixture.Content);
            Assert.HasCount(1, execution.OriginalMutations);
            Assert.AreEqual(original.OperationId, execution.OriginalMutations[0].OperationId);
            var transferred = (await catalog.ReadMutationAsync(original.OperationId))!;
            Assert.AreEqual(original, transferred.Intent);
            Assert.AreEqual(MirrorPulseMutationState.Superseded, transferred.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, transferred.ExecutionEvidence);
            Assert.AreEqual(fixture.Plan.PlanId, transferred.SupersededByPlanId);
            foreach (var step in execution.Steps) Assert.IsNull(await catalog.ReadMutationAsync(step.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = await reopened.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(fixture.Plan,
            fixture.Window, [original], fixture.Revision, fixture.Content);
        Assert.AreEqual(JsonSerializer.Serialize(execution), JsonSerializer.Serialize(replay));
        foreach (var member in fixture.Plan.Members)
            Assert.AreEqual(fixture.Plan.PlanId, (await reopened.ReadJournalCoalescingOwnerAsync(member.OperationId))!.PlanId);
    }

    [TestMethod]
    [DataRow("baseline")]
    [DataRow("binding")]
    [DataRow("late-effect-collision")]
    public async Task LatePreparationFailureRollsBackSupersedingAndAllOriginalReservations(string scenario)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await catalog.PrepareMutationAsync(fixture.Intent(fixture.Window[0]));
        var expected = MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        MirrorPulseMutationRecord? collision = null;
        if (scenario == "late-effect-collision")
            collision = await catalog.PrepareMutationAsync(original.Intent with { OperationId = expected.Steps[1].OperationId });
        var content = scenario == "binding" ? fixture.Content! with
        { UploadBinding = fixture.Content!.UploadBinding with { LocalObject = fixture.Content.UploadBinding.LocalObject with { LocalFileId = Guid.NewGuid() } } }
            : fixture.Content;
        if (scenario == "late-effect-collision")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(
                fixture.Plan, fixture.Window, [original.Intent], fixture.Revision, content));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(
                fixture.Plan, fixture.Window, [original.Intent], scenario == "baseline" ? "changed" : fixture.Revision, content));
        Assert.AreEqual(original, await catalog.ReadMutationAsync(original.Intent.OperationId));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(member.OperationId));
        Assert.IsNull(await catalog.ReadMutationAsync(expected.Steps[0].OperationId));
        if (collision is not null) Assert.AreEqual(collision, await catalog.ReadMutationAsync(collision.Intent.OperationId));
        else
            await catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(fixture.Plan,
                fixture.Window, [original.Intent], fixture.Revision, fixture.Content);
    }

    [TestMethod]
    [DataRow("executing")]
    [DataRow("ambiguous")]
    [DataRow("accepted")]
    [DataRow("conflict")]
    [DataRow("prepared-after-start")]
    [DataRow("unknown")]
    public async Task AnIntentThatMayHaveStartedCannotBeTransferred(string scenario)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = fixture.Intent(fixture.Window[0]); await catalog.PrepareMutationAsync(original);
        if (scenario == "unknown")
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "UPDATE mutation_intents SET execution_started=NULL WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", original.OperationId.ToString("D")); await query.ExecuteNonQueryAsync();
        }
        else
        {
            await catalog.TransitionMutationAsync(original.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            if (scenario != "executing")
                await catalog.TransitionMutationAsync(original.OperationId, MirrorPulseMutationState.Executing, scenario switch
                {
                    "ambiguous" => MirrorPulseMutationState.Ambiguous,
                    "accepted" => MirrorPulseMutationState.RemoteAccepted,
                    "conflict" => MirrorPulseMutationState.Conflict,
                    _ => MirrorPulseMutationState.Prepared,
                });
        }
        var before = await catalog.ReadMutationAsync(original.OperationId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(
            fixture.Plan, fixture.Window, [original], fixture.Revision, fixture.Content));
        Assert.AreEqual(before, await catalog.ReadMutationAsync(original.OperationId));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
    }

    [TestMethod]
    public async Task ExplicitTransferCannotOmitAnotherPreparedOriginalOrRewriteTheRetainedSetOnReplay()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var originals = fixture.Window.Select(fixture.Intent).ToArray();
        var records = new List<MirrorPulseMutationRecord>();
        foreach (var intent in originals) records.Add(await catalog.PrepareMutationAsync(intent));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(
            fixture.Plan, fixture.Window, [originals[0]], fixture.Revision, fixture.Content));
        foreach (var record in records) Assert.AreEqual(record, await catalog.ReadMutationAsync(record.Intent.OperationId));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        var execution = await catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(fixture.Plan,
            fixture.Window, originals, fixture.Revision, fixture.Content);
        Assert.HasCount(2, execution.OriginalMutations);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingAndExecutionWithUnstartedIntentsAsync(
            fixture.Plan, fixture.Window, [originals[0]], fixture.Revision, fixture.Content));
        Assert.AreEqual(JsonSerializer.Serialize(execution), JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }
}
