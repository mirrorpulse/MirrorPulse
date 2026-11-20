using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    public async Task RejectedDeletionPublishesANewWholeGroupEpochWithoutRewritingOriginalFactsOrRetryingAutomatically()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        string original = JsonSerializer.Serialize(decision.Intent);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
        var observation = (await catalog.ReadConflictDeletionObservationAsync(decision.Intent.RemoteDeletion.OperationId))!;
        Assert.AreEqual("concurrent-delete", observation.ExpectedRevision); Assert.AreEqual("later-remote", observation.ObservedRevision);
        Assert.AreEqual(MirrorPulseConflictDeletionObservationOrigin.WorkerRejection, observation.Origin);
        var epoch = await catalog.ReadUploadConflictsAsync();
        Assert.HasCount(decision.Intent.OriginalOperationIds.Count, epoch);
        CollectionAssert.AreEquivalent(decision.Intent.OriginalOperationIds.Select(id => id.ToString("D")).ToArray(), epoch.Select(conflict => conflict.ChangeId).ToArray());
        Assert.IsTrue(epoch.All(conflict => conflict.RemoteRevision == "later-remote" && !decision.Intent.ConflictIds.Contains(conflict.ConflictId)));
        foreach (Guid id in decision.Intent.ConflictIds)
            Assert.IsFalse((await catalog.ReadUploadConflictAsync(id))!.IsPending);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask).AsTask());
        Assert.IsEmpty(remote.Writes); Assert.AreEqual(observation, await catalog.ReadConflictDeletionObservationAsync(observation.OperationId));
        Assert.AreEqual(original, JsonSerializer.Serialize((await catalog.ReadConflictDeletionAsync(decision.Intent.CommandId))!.Intent));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitDeletionRetryUsesADistinctConditionalEffectAndRecoversWithoutAnotherDelete(bool loseReply)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        MirrorPulseConflictDeletion decision; MirrorPulseConflictDeletionRetry retry; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            decision = await PrepareDeletionAsync(catalog, fixture, remote);
            original = JsonSerializer.Serialize(decision.Intent);
            await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
            retry = await AuthorizeLatestDeletionAsync(catalog, decision);
            Assert.AreNotEqual(decision.Intent.RemoteDeletion.OperationId, retry.Intent.OperationId);
            Assert.AreEqual("later-remote", retry.Intent.ExpectedRevision);
            Assert.AreEqual(retry, await catalog.AuthorizeConflictDeletionRetryAsync(retry.CommandId, decision.Intent.CommandId, retry.ConflictId));
            remote.LoseReplyAfter = loseReply ? "Delete" : null;
            if (loseReply)
                await Assert.ThrowsExactlyAsync<IOException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
                    (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask).AsTask());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        int acknowledgements = 0;
        await DeletionExecutor(reopened, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(reopened, value, token), (intent, _) =>
            {
                acknowledgements++; CollectionAssert.AreEquivalent(decision.Intent.OriginalOperationIds.ToArray(), intent.OriginalOperationIds.ToArray());
                return ValueTask.CompletedTask;
            });
        Assert.AreEqual(1, acknowledgements); Assert.HasCount(1, remote.Writes); Assert.AreEqual(retry.Intent.OperationId, remote.Writes[0].Id);
        Assert.AreEqual("Delete", remote.Writes[0].Kind); Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(retry.CommandId))!.State);
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(decision.Intent.CommandId))!.State);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await reopened.ReadMutationAsync(decision.Intent.RemoteDeletion.OperationId))!.State);
        Assert.AreEqual(original, JsonSerializer.Serialize((await reopened.ReadConflictDeletionAsync(decision.Intent.CommandId))!.Intent));
        Assert.IsEmpty(await reopened.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task ASecondRejectionRequiresAnotherCurrentEpochAndCannotReuseTheFirstChoice()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
        var first = await AuthorizeLatestDeletionAsync(catalog, decision);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "changed-again");
        var epoch = (await catalog.ReadUploadConflictsAsync())[0]; Assert.AreNotEqual(first.ConflictId, epoch.ConflictId);
        Guid stale = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(stale, "deleteremote", first.ConflictId.ToString("D"), "pending"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeConflictDeletionRetryAsync(stale, decision.Intent.CommandId, first.ConflictId));
        Assert.AreEqual(first.Intent, await catalog.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        var next = await AuthorizeLatestDeletionAsync(catalog, decision);
        Assert.AreEqual(first.Intent.OperationId, next.PreviousOperationId); Assert.AreEqual("changed-again", next.Intent.ExpectedRevision);
        Assert.AreEqual(next.Intent, await catalog.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        await DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask);
        Assert.HasCount(1, remote.Writes); Assert.AreEqual(next.Intent.OperationId, remote.Writes[0].Id);
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(first.CommandId))!.State);
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(next.CommandId))!.State);
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    public async Task AnUnknownOrUnrejectedDeletionCannotReceiveANewConditionalAttempt(MirrorPulseMutationState state)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await CompleteSyntheticLocalRetirementAsync(catalog, decision, default);
        if (state != MirrorPulseMutationState.Prepared)
            await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        if (state is MirrorPulseMutationState.Ambiguous or MirrorPulseMutationState.RemoteAccepted)
            await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Executing, state);
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "deleteremote", decision.Intent.ConflictId.ToString("D"), "pending"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeConflictDeletionRetryAsync(command, decision.Intent.CommandId, decision.Intent.ConflictId));
        Assert.AreEqual(decision.Intent.RemoteDeletion, await catalog.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnExplicitlyObservedAbsenceNeverTurnsIntoAnUnconditionalDelete(bool reappeared)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        remote.RemoveExternally(fixture.Plan.FinalPath);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask).AsTask());
        var retry = await AuthorizeLatestDeletionAsync(catalog, decision); Assert.IsNull(retry.Intent.ExpectedRevision);
        if (reappeared)
        {
            remote.Seed(fixture.Plan.FinalPath, "new-object", [4, 5]);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
                (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => throw new AssertFailedException("A new occupant cannot be acknowledged.")).AsTask());
            Assert.IsTrue(remote.Exists(fixture.Plan.FinalPath)); Assert.HasCount(decision.Intent.OriginalOperationIds.Count, await catalog.ReadUploadConflictsAsync());
        }
        else
        {
            await DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
                (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask);
            Assert.IsEmpty(await catalog.ReadUploadConflictsAsync());
        }
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("observation")]
    [DataRow("previous")]
    public async Task CorruptDeletionRetryEvidenceCannotReachPreservationRemoteOrAcknowledgement(string damage)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
        _ = await AuthorizeLatestDeletionAsync(catalog, decision);
        await ExecuteRetrySqlAsync(fixture, damage switch
        {
            "fingerprint" => "UPDATE conflict_deletion_retries SET fingerprint=zeroblob(32);",
            "observation" => "DELETE FROM conflict_deletion_observations;",
            _ => "DELETE FROM mutation_intents WHERE state=5;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (_, _) => throw new AssertFailedException("Invalid evidence must fail before local work."),
            (_, _) => throw new AssertFailedException("Invalid evidence cannot acknowledge originals.")).AsTask());
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    public async Task ARejectionWithoutHistoricalObservationUsesReadbackAndStillWaitsForAFreshUserChoice()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await CompleteSyntheticLocalRetirementAsync(catalog, decision, default);
        await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
        remote.Seed(fixture.Plan.FinalPath, "readback-current", [7, 7]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => ValueTask.CompletedTask).AsTask());
        var observation = (await catalog.ReadConflictDeletionObservationAsync(decision.Intent.RemoteDeletion.OperationId))!;
        Assert.AreEqual(MirrorPulseConflictDeletionObservationOrigin.Readback, observation.Origin); Assert.AreEqual("readback-current", observation.ObservedRevision);
        Assert.IsEmpty(remote.Writes); Assert.AreEqual(decision.Intent.RemoteDeletion, await catalog.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        var retry = await AuthorizeLatestDeletionAsync(catalog, decision); Assert.AreEqual("readback-current", retry.Intent.ExpectedRevision);
    }

    private static async Task RejectDeletionAsync(MirrorPulseProductCatalog catalog, Fixture fixture, FileRemote remote,
        MirrorPulseConflictDeletion decision, string revision)
    {
        remote.Seed(fixture.Plan.FinalPath, revision, [8, 8]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => DeletionExecutor(catalog, remote).ExecuteAsync(decision.Intent.CommandId,
            (value, token) => CompleteSyntheticLocalRetirementAsync(catalog, value, token), (_, _) => throw new AssertFailedException("Rejected deletion cannot acknowledge originals.")).AsTask());
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("target")]
    [DataRow("state")]
    public async Task ADeletionRetryRequiresAMatchingPendingUserCommand(string mismatch)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
        var conflict = (await catalog.ReadUploadConflictsAsync())[0]; Guid command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, mismatch == "action" ? "keepremote" : "deleteremote",
            (mismatch == "target" ? Guid.NewGuid() : conflict.ConflictId).ToString("D"), mismatch == "state" ? "failed" : "pending"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AuthorizeConflictDeletionRetryAsync(command, decision.Intent.CommandId, conflict.ConflictId));
        Assert.AreEqual(decision.Intent.RemoteDeletion, await catalog.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    public async Task ADeletionObservationAndItsPublicEpochCannotBeRewrittenAfterRetention()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var decision = await PrepareDeletionAsync(catalog, fixture, remote);
        await RejectDeletionAsync(catalog, fixture, remote, decision, "later-remote");
        var observation = (await catalog.ReadConflictDeletionObservationAsync(decision.Intent.RemoteDeletion.OperationId))!;
        var epoch = await catalog.ReadUploadConflictsAsync();
        await catalog.RecordConflictDeletionObservationAsync(observation);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordConflictDeletionObservationAsync(observation with { ObservedRevision = "unseen" }));
        CollectionAssert.AreEquivalent(epoch.Select(conflict => conflict.ConflictId).ToArray(),
            (await catalog.ReadUploadConflictsAsync()).Select(conflict => conflict.ConflictId).ToArray());
        Assert.AreEqual(observation, await catalog.ReadConflictDeletionObservationAsync(observation.OperationId));
    }

    [TestMethod]
    public async Task Schema56UpgradePreservesARejectedDeletionAndDoesNotInventItsLostRemoteObservation()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        MirrorPulseConflictDeletion decision;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            decision = await PrepareDeletionAsync(catalog, fixture, remote);
            await CompleteSyntheticLocalRetirementAsync(catalog, decision, default);
            await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await catalog.TransitionMutationAsync(decision.Intent.RemoteDeletion.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
        }
        await ExecuteRetrySqlAsync(fixture, "DROP TABLE conflict_deletion_retries; DROP TABLE conflict_deletion_observations; PRAGMA user_version=56;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(decision.Intent), JsonSerializer.Serialize((await reopened.ReadConflictDeletionAsync(decision.Intent.CommandId))!.Intent));
        Assert.AreEqual(decision.Intent.RemoteDeletion, await reopened.ReadCurrentConflictDeletionEffectAsync(decision.Intent.CommandId));
        Assert.IsNull(await reopened.ReadConflictDeletionObservationAsync(decision.Intent.RemoteDeletion.OperationId));
        CollectionAssert.AreEquivalent(decision.Intent.ConflictIds.ToArray(), (await reopened.ReadUploadConflictsAsync()).Select(conflict => conflict.ConflictId).ToArray());
        Assert.IsEmpty(remote.Writes);
    }

    private static async Task<MirrorPulseConflictDeletionRetry> AuthorizeLatestDeletionAsync(MirrorPulseProductCatalog catalog, MirrorPulseConflictDeletion decision)
    {
        var conflict = (await catalog.ReadUploadConflictsAsync())[0]; Guid command = Guid.NewGuid();
        await catalog.SaveUserCommandAsync(new(command, "deleteremote", conflict.ConflictId.ToString("D"), "pending"));
        return await catalog.AuthorizeConflictDeletionRetryAsync(command, decision.Intent.CommandId, conflict.ConflictId);
    }
}
