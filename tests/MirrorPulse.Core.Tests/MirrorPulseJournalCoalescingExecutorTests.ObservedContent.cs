using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    public async Task AcceptedBytesKeepTheWholeMoveAndUpdatePlanWhileSelectingOnlyItsNecessaryMove()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile); var remote = new FileRemote(fixture);
        remote.Seed(fixture.Plan.OriginalPath, fixture.Revision!, FinalBytes);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        string originalPlan = JsonSerializer.Serialize(fixture.Plan), originalWindow = JsonSerializer.Serialize(fixture.Window);
        var execution = await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
        Assert.AreEqual(3, execution.Version); Assert.IsNull(execution.Content); Assert.HasCount(1, execution.Steps);
        Assert.AreEqual(MirrorPulseCoalescingStepKind.Move, execution.Steps[0].Kind);
        Assert.AreEqual(observed, execution.ObservedContent);
        var result = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("An accepted unchanged-content move must not upload again."));
        Assert.HasCount(1, result); Assert.HasCount(1, remote.Writes); Assert.AreEqual("Move", remote.Writes[0].Kind);
        Assert.AreEqual(observed.Content.UploadBinding, result[0].Intent.UploadBinding);
        Assert.AreEqual(observed.Content.Sha256, result[0].Intent.ContentSha256);
        Assert.AreEqual("moved/" + fixture.Revision, result[0].AcceptedRevision);
        Assert.AreEqual(originalPlan, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingPlanAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(originalWindow, JsonSerializer.Serialize(fixture.Window));
        CollectionAssert.AreEqual(FinalBytes, remote.Read(fixture.Plan.FinalPath));
    }

    [TestMethod]
    public async Task AnObservedMoveConflictStillRequiresAnExplicitMoveAndContentDecision()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile); var remote = new FileRemote(fixture);
        remote.Seed(fixture.Plan.OriginalPath, "changed", [8, 8]);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        var execution = await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var choice = await AuthorizeVerificationDecisionAsync(catalog, fixture, observed.Content);
        Assert.AreEqual(3, choice.Version); Assert.IsNotNull(choice.FollowupOperationId);
        Assert.IsNull(choice.Intent.UploadBinding);
        var result = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        Assert.HasCount(2, result); CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(value => value.Kind).ToArray());
        Assert.AreEqual("moved/changed", result[1].Intent.ExpectedRevision);
        Assert.AreEqual(observed.Content.UploadBinding, result[1].Intent.UploadBinding);
        Assert.AreEqual(JsonSerializer.Serialize(execution), JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    public async Task OnlyTheOriginalObservedMoveCanConfirmItsUnchangedBytesAtTheActualAcceptedRevision()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile); var remote = new FileRemote(fixture);
        remote.Seed(fixture.Plan.OriginalPath, fixture.Revision!, FinalBytes);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        var seed = await catalog.ReadContentAcceptanceProofAsync(observed.AcceptanceOperationId);
        await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
        var results = await Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("An observed move must not upload."), async (move, token) =>
            {
                var proof = new MirrorPulseContentAcceptanceProof(move.Intent.OperationId, observed.Content.UploadBinding,
                    observed.ItemId, observed.RemoteId, move.AcceptedRevision, observed.Content.Length, observed.Content.Sha256);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveContentAcceptanceProofAsync(proof with { RemoteId = "unrelated-target" }, token));
                await catalog.SaveContentAcceptanceProofAsync(proof, token);
            });
        var accepted = (await catalog.ReadContentAcceptanceProofAsync(results[0].Intent.OperationId))!;
        Assert.AreEqual(results[0].AcceptedRevision, accepted.AcceptedRevision);
        Assert.AreNotEqual(observed.RemoteRevision, accepted.AcceptedRevision);
        Assert.AreEqual(seed, await catalog.ReadContentAcceptanceProofAsync(observed.AcceptanceOperationId));
    }

    [TestMethod]
    public async Task AcceptedCurrentBytesSelectVerificationWithoutChangingAnyOriginalNotificationOrPlan()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        remote.Seed(fixture.Plan.FinalPath, fixture.Revision!, FinalBytes);
        string executionJson;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
            var lookup = await catalog.ReadAcknowledgedContentProofAsync(fixture.Plan.InstanceId, fixture.Plan.RootKey,
                fixture.Plan.ItemId, fixture.Revision!, observed.Content.UploadBinding.LocalObject);
            Assert.AreEqual(observed.AcceptanceOperationId, lookup!.OperationId);
            string planJson = JsonSerializer.Serialize(fixture.Plan);
            var execution = await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
            Assert.AreEqual(2, execution.Version); Assert.IsNull(execution.Content); Assert.AreEqual(observed, execution.ObservedContent);
            Assert.HasCount(1, execution.Steps); Assert.AreEqual(MirrorPulseCoalescingStepKind.VerifyUnchanged, execution.Steps[0].Kind);
            Assert.AreEqual(planJson, JsonSerializer.Serialize(await catalog.ReadJournalCoalescingPlanAsync(fixture.Plan.PlanId)));
            Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile, fixture.Plan.Effect);
            Assert.IsTrue(fixture.Window.All(command => command.Kind == MirrorPulseWorkerChangeKind.ContentUpdate));
            executionJson = JsonSerializer.Serialize(execution);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId);
        Assert.AreEqual(executionJson, JsonSerializer.Serialize(retained));
        var result = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("Net-unchanged verification must not open an upload."));
        Assert.AreEqual(MirrorPulseMutationState.Acknowledged, result[0].State); Assert.IsEmpty(remote.Writes);
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await reopened.ReadMutationAsync(member.OperationId));
        Assert.AreEqual(executionJson, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("length")]
    [DataRow("binding")]
    [DataRow("placeholder")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("revision")]
    [DataRow("missing-proof")]
    [DataRow("receipt")]
    [DataRow("root")]
    public async Task AnUnprovedContentObservationRollsBackAllOriginalAndEffectOwnership(string change)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture, change == "root" ? "another-root" : null);
        observed = change switch
        {
            "hash" => observed with { Content = observed.Content with { Sha256 = new string('F', 64) } },
            "length" => observed with { Content = observed.Content with { Length = 123 } },
            "binding" => observed with { Content = observed.Content with { UploadBinding = observed.Content.UploadBinding with { LocalObject = new(42, Guid.NewGuid(), Guid.NewGuid()) } } },
            "placeholder" => observed with { Content = observed.Content with { UploadBinding = observed.Content.UploadBinding with { PreviousPlaceholderIdentity = Convert.ToBase64String([4, 3, 2, 1]) } } },
            "item" => observed with { ItemId = Guid.NewGuid() },
            "remote" => observed with { RemoteId = "unrelated-remote" },
            "revision" => observed with { RemoteRevision = "unrelated-revision" },
            "missing-proof" => observed with { AcceptanceOperationId = Guid.NewGuid() },
            _ => observed,
        };
        if (change == "receipt")
            await ExecuteRetrySqlAsync(fixture, "UPDATE content_acceptance_proofs SET receipt=NULL;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(member.OperationId));
    }

    [TestMethod]
    public async Task ObservedVerificationStillRejectsChangedRemoteContentBeforeAnyWriteOrOriginalAcknowledgement()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        var execution = await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
        remote.Seed(fixture.Plan.FinalPath, "concurrent-remote", [9, 9]);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("Verification must not upload.")).AsTask());
        Assert.IsEmpty(remote.Writes); Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(execution.Steps[0].OperationId))!.State);
        Assert.AreEqual(observed, (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!.ObservedContent);
        foreach (var member in fixture.Plan.Members) Assert.IsNull(await catalog.ReadMutationAsync(member.OperationId));
    }

    [TestMethod]
    public async Task APreparedOriginalCannotBeDiscardedByAnObservedContentProgram()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        var intent = fixture.Intent(fixture.Window[0]); var original = await catalog.PrepareMutationAsync(intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed));
        Assert.AreEqual(original, await catalog.ReadMutationAsync(intent.OperationId)); Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
    }

    [TestMethod]
    public async Task RemovingHistoricalAcceptancePreventsExecutionReplayWithoutInventingAnotherProof()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await SeedAcknowledgedObservationAsync(catalog, fixture);
        var execution = await catalog.PrepareJournalCoalescingObservedExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision!, observed);
        await ExecuteRetrySqlAsync(fixture, "DELETE FROM content_acceptance_proofs;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        Assert.IsNull(await catalog.ReadMutationAsync(execution.Steps[0].OperationId));
    }

    private static async Task<MirrorPulseCoalescingObservedContent> SeedAcknowledgedObservationAsync(MirrorPulseProductCatalog catalog, Fixture fixture, string? root = null)
    {
        var content = fixture.Content!; Guid accepted = Guid.NewGuid();
        var intent = new MirrorPulseMutationIntent(accepted, fixture.Plan.InstanceId, root ?? fixture.Plan.RootKey,
            MirrorPulseWorkerChangeKind.ContentUpdate, fixture.Plan.FinalPath, null, false, "previous", content.Length,
            content.Sha256, MirrorPulseMutationOrigin.Rescan, content.UploadBinding);
        var proof = new MirrorPulseContentAcceptanceProof(accepted, content.UploadBinding, fixture.Plan.ItemId, "accepted-file",
            fixture.Revision, content.Length, content.Sha256);
        string identity = Convert.ToBase64String(new CloudPlaceholderIdentity(proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision!).Encode());
        await new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent, _ => ValueTask.FromResult(fixture.Revision), async (_, token) =>
        {
            await catalog.SaveContentAcceptanceProofAsync(proof, token);
            // Native confirmation is a Core fixture premise; production native
            // tests separately prove the actual protected SDK confirmation.
            await catalog.SaveContentConfirmationReceiptAsync(accepted, new(MirrorPulseContentConfirmationOutcome.Confirmed,
                MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete, true, true, true, true,
                content.Length, 1, null, null, null, null, true, identity, DateTimeOffset.UtcNow), token);
        }, default);
        var current = content with { UploadBinding = new(content.UploadBinding.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, identity) };
        return new(accepted, current, proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision!);
    }
}
