using System.Text.Json;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalCoalescingAtomicPreparationTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task CompletePreparationAndOwnershipSurviveReopeningForEveryEffect(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect);
        MirrorPulseJournalCoalescingExecution prepared;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            prepared = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window,
                fixture.Revision, fixture.Content);
            Assert.AreEqual(fixture.Content, prepared.Content);
            Assert.IsEmpty(await catalog.ReadIncompleteMutationsAsync(), "Preparation cannot dispatch or accept an effect.");
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(fixture.Plan), JsonSerializer.Serialize(await reopened.ReadJournalCoalescingPlanAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(
            await reopened.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content)));
        foreach (var original in fixture.Window)
        {
            Assert.AreEqual(fixture.Plan.PlanId, (await reopened.ReadJournalCoalescingOwnerAsync(original.OperationId))!.PlanId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareMutationAsync(fixture.Intent(original)));
        }
        Assert.IsNull(await reopened.ReadJournalCoalescingOwnerAsync(Guid.NewGuid()));
        foreach (var step in prepared.Steps)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareMutationAsync(fixture.Intent(fixture.Window[0]) with { OperationId = step.OperationId }));
    }

    [TestMethod]
    public async Task LateEffectCollisionRollsBackPlanAllOriginalIdsAndEarlierEffects()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var expected = MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var collision = fixture.Intent(fixture.Window[0]) with { OperationId = expected.Steps[1].OperationId };
        await catalog.PrepareMutationAsync(collision);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionAsync(
            fixture.Plan, fixture.Window, fixture.Revision, fixture.Content));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        Assert.AreEqual(collision, (await catalog.ReadMutationAsync(collision.OperationId))!.Intent);
        foreach (var original in fixture.Window) await catalog.PrepareMutationAsync(fixture.Intent(original));
        await catalog.PrepareMutationAsync(collision with { OperationId = expected.Steps[0].OperationId });
        await catalog.PrepareMutationAsync(collision with { OperationId = fixture.Plan.PlanId });
    }

    [TestMethod]
    public async Task ExistingUnstartedMutationIsNotSilentlySupersededByAtomicPreparation()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var intent = fixture.Intent(fixture.Window[1]);
        var original = await catalog.PrepareMutationAsync(intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAndExecutionAsync(
            fixture.Plan, fixture.Window, fixture.Revision, fixture.Content));
        Assert.AreEqual(original, await catalog.ReadMutationAsync(intent.OperationId));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        await catalog.PrepareMutationAsync(fixture.Intent(fixture.Window[0]));
    }

    [TestMethod]
    public async Task ConcurrentDifferentFinalContentHasOneCompleteDurableWinner()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<MirrorPulseJournalCoalescingExecution?> PrepareAsync(bool changed)
        {
            await start.Task;
            try
            {
                return await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision,
                    changed ? fixture.Content! with { Sha256 = new string('B', 64) } : fixture.Content);
            }
            catch (InvalidDataException) { return null; }
        }
        var first = PrepareAsync(false); var second = PrepareAsync(true); start.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.HasCount(1, results.Where(result => result is not null).ToArray());
        Assert.HasCount(1, await catalog.ReadJournalCoalescingPlansAsync());
        Assert.AreEqual(JsonSerializer.Serialize(results.Single(result => result is not null)),
            JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    public async Task CancelledPreparationDoesNotReserveAnyOriginalJournalId()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareJournalCoalescingAndExecutionAsync(
            fixture.Plan, fixture.Window, fixture.Revision, fixture.Content, cancelled.Token));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        foreach (var original in fixture.Window) await catalog.PrepareMutationAsync(fixture.Intent(original));
    }
}
