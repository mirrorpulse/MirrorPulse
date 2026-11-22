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
    public async Task FailedOwnerCompletionRetainsAcceptedEffectsAndStopsLaterWritesAcrossOwners(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture);
        MirrorPulseMutationRecord? original = null; MirrorPulseMutationRecord? accepted = null;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareAsync(first, fixture); original = await first.ReadMutationAsync(fixture.Window[0].OperationId);
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(first, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent,
                (record, _) =>
                {
                    Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, record.State);
                    accepted = record; throw new IOException("Injected durable local projection failure.");
                }).AsTask());
            Assert.IsNotNull(accepted); Assert.AreEqual(accepted, await first.ReadMutationAsync(accepted.Intent.OperationId));
            var preparation = (await first.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
            if (preparation.Steps.Count == 2)
            {
                Assert.HasCount(preparation.Steps[0].Kind == MirrorPulseCoalescingStepKind.Move ? 1 : 0, remote.Writes);
                Assert.IsNull(await first.ReadMutationAsync(preparation.Steps[1].OperationId));
            }
        }
        int writesBeforeRecovery = remote.Writes.Count; var completedIds = new List<Guid>();
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(second, remote).ExecuteAsync(fixture.Plan.PlanId,
            (content, token) => effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile ? OpenContent(content, token) :
                throw new AssertFailedException("An accepted upload must not reopen or resend local bytes."),
            (record, _) =>
            {
                Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, record.State);
                if (completedIds.Count == 0) Assert.AreEqual(accepted!.Intent, record.Intent);
                completedIds.Add(record.Intent.OperationId); return ValueTask.CompletedTask;
            });
        Assert.HasCount(writesBeforeRecovery + (effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile ? 1 : 0), remote.Writes);
        Assert.HasCount(completed.Count, completedIds);
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.AreEqual(original, await second.ReadMutationAsync(fixture.Window[0].OperationId));
        Assert.AreEqual(MirrorPulseMutationState.Superseded, original!.State);
        Assert.IsTrue(completedIds.All(id => fixture.Window.All(command => command.OperationId != id)));
        await Executor(second, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("Completed effects cannot read local content."),
            (_, _) => throw new AssertFailedException("Completed effect history cannot repeat owner completion."));
    }

    [TestMethod]
    public async Task CancellationInOwnerCompletionRetainsAcceptedUploadWithoutResendingAfterRestart()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        Guid step;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareAsync(first, fixture);
            step = (await first.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!.Steps[0].OperationId;
            using var cancelled = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Executor(first, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent,
                (record, token) => { Assert.AreEqual(step, record.Intent.OperationId); cancelled.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; },
                cancelled.Token).AsTask());
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await first.ReadMutationAsync(step))!.State);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        int completions = 0;
        await Executor(second, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("The remote upload has already been accepted."),
            (record, _) => { Assert.AreEqual(step, record.Intent.OperationId); completions++; return ValueTask.CompletedTask; });
        Assert.AreEqual(1, completions); Assert.HasCount(1, remote.Writes);
        Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await second.ReadMutationAsync(step))!.State);
        Assert.AreEqual(MirrorPulseMutationState.Superseded, (await second.ReadMutationAsync(fixture.Window[0].OperationId))!.State);
    }
}
