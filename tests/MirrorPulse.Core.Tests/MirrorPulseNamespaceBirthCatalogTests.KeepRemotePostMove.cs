using System.Text.Json;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteCurrentContentDecisionDoesNotInventAnAcknowledgedMovePrefix()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog);
        Assert.IsNull(await catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(setup.Preparation.Intent.CommandId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(Guid.Empty));
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    public async Task KeepRemotePostMoveStartCannotBorrowAnUnsettledOriginalPrefix(MirrorPulseMutationState changedState)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog, moveAcceptedBeforeConflict: true);
        var execution = await catalog.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId); Assert.IsNotNull(execution);
        Guid originalMove = execution.Steps[0].OperationId;
        // Fault injection changes the original prefix head, rather than substituting a
        // fresh move or rewriting the accepted command's immutable effect selection.
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE mutation_intents SET state={(int)changedState} WHERE operation_id='{originalMove:D}';");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(setup.Preparation.Intent.CommandId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.AreEqual(changedState, (await catalog.ReadMutationAsync(originalMove))!.State);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(setup.Preparation.Intent.CommandId))!.State);
    }

    [TestMethod]
    public async Task KeepRemotePostMoveStartCannotReplaceTheAcknowledgedPrefixRevision()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog, moveAcceptedBeforeConflict: true);
        var execution = await catalog.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId); Assert.IsNotNull(execution);
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE mutation_intents SET accepted_revision='another-prefix-revision' WHERE operation_id='{execution.Steps[0].OperationId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(setup.Preparation.Intent.CommandId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.AreEqual("moved-baseline", setup.Preparation.Intent.RejectedEffect.ExpectedRevision);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(setup.Preparation.Intent.CommandId))!.State);
    }

    [TestMethod]
    public async Task KeepRemoteResidentStartCannotTreatARejectedMoveAsAcknowledgedContentWork()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog, rejectMove: true);
        Assert.AreEqual(MirrorPulseWorkerChangeKind.Move, setup.Preparation.Intent.RejectedEffect.Kind);
        string rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(setup.Preparation.Intent.RejectedEffect.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await catalog.ReadMutationAsync(setup.Preparation.Intent.RejectedEffect.OperationId)));
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(setup.Preparation.Intent.CommandId))!.State);
        Assert.HasCount(2, await catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task KeepRemoteResidentStartAfterAcknowledgedMoveRetainsItsOriginalEffectAndCompleteJournalAcrossOwners()
    {
        using var fixture = new Fixture(); KeepRemoteFileSetup setup;
        string moveHistory; string rejectedHistory; string journalHistory;
        MirrorPulseConflictKeepRemoteFileStart start;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteFileAsync(catalog, moveAcceptedBeforeConflict: true);
            var plan = await catalog.ReadJournalCoalescingOwnerAsync(setup.Preparation.Intent.OriginalOperationIds[0]);
            Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.MoveAndUpdateFile, plan.Effect);
            var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId); Assert.IsNotNull(execution);
            Assert.HasCount(2, execution.Steps);
            var moved = await catalog.ReadMutationAsync(execution.Steps[0].OperationId); Assert.IsNotNull(moved);
            Assert.AreEqual(MirrorPulseMutationState.Acknowledged, moved.State);
            Assert.AreEqual("moved-baseline", moved.AcceptedRevision);
            Assert.AreEqual(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteAcknowledgedMoveAsync(setup.Preparation.Intent.CommandId)));
            Assert.AreEqual(MirrorPulseWorkerChangeKind.ContentUpdate, setup.Preparation.Intent.RejectedEffect.Kind);
            Assert.AreEqual("moved-baseline", setup.Preparation.Intent.RejectedEffect.ExpectedRevision);
            moveHistory = JsonSerializer.Serialize(moved);
            rejectedHistory = JsonSerializer.Serialize(await catalog.ReadMutationAsync(execution.Steps[1].OperationId));
            journalHistory = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteJournalAsync(setup.Preparation.Intent.CommandId));
            start = await StartKeepRemoteFileAsync(catalog, setup);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteFileStartAsync(start.CommandId)));
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await StartKeepRemoteFileAsync(reopened, setup)));
        var originalExecution = await reopened.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId); Assert.IsNotNull(originalExecution);
        Assert.AreEqual(moveHistory, JsonSerializer.Serialize(await reopened.ReadMutationAsync(originalExecution.Steps[0].OperationId)));
        Assert.AreEqual(moveHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteAcknowledgedMoveAsync(start.CommandId)));
        Assert.AreEqual(rejectedHistory, JsonSerializer.Serialize(await reopened.ReadMutationAsync(originalExecution.Steps[1].OperationId)));
        Assert.AreEqual(journalHistory, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteJournalAsync(start.CommandId)));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(start.CommandId))!.State);
        Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
