using System.Text.Json;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Acknowledged)]
    public async Task KeepRemoteCreateCannotBorrowAnUnrejectedOrAlreadyAcceptedOriginal(MirrorPulseMutationState changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog, createFile: true);
        Guid effect = setup.Preparation.Intent.RejectedEffect.OperationId;
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE mutation_intents SET state={(int)changed} WHERE operation_id='{effect:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(changed, (await catalog.ReadMutationAsync(effect))!.State);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(setup.Preparation.Intent.CommandId))!.State);
    }

    [TestMethod]
    public async Task KeepRemoteResidentCreateStartRetainsItsRejectedAbsenceConditionAndAllOriginalsAcrossOwners()
    {
        using var fixture = new Fixture(); KeepRemoteFileSetup setup;
        MirrorPulseConflictKeepRemoteFileStart start; string rejected; string journal;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteFileAsync(catalog, createFile: true);
            var original = setup.Preparation.Intent;
            var plan = await catalog.ReadJournalCoalescingOwnerAsync(original.OriginalOperationIds[0]);
            Assert.IsNotNull(plan); Assert.AreEqual(MirrorPulseCoalescedEffect.CreateFile, plan.Effect);
            Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, original.RejectedEffect.Kind);
            Assert.IsNull(original.RejectedEffect.ExpectedRevision);
            var birth = await catalog.ReadConflictKeepRemoteOriginalBirthAsync(original.CommandId);
            Assert.IsNotNull(birth); Assert.AreEqual(setup.Original.Context.Member.OriginId, birth.OperationId);
            Assert.AreEqual(original.ItemId, birth.ItemId);
            var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId);
            Assert.IsNotNull(execution); Assert.HasCount(1, execution.Steps);
            rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(original.RejectedEffect.OperationId));
            journal = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteJournalAsync(original.CommandId));
            start = await StartKeepRemoteFileAsync(catalog, setup);
            Assert.AreEqual(setup.Original, start.Original); Assert.AreEqual(setup.Parent, start.Parent);
            Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(original.CommandId))!.State);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await reopened.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await reopened.ReadMutationAsync(setup.Preparation.Intent.RejectedEffect.OperationId)));
        Assert.AreEqual(journal, JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteJournalAsync(setup.Preparation.Intent.CommandId)));
        foreach (Guid original in setup.Preparation.Intent.OriginalOperationIds)
            Assert.AreEqual(setup.Preparation.Intent.CommandId, await reopened.ReadConflictKeepRemoteOwnerAsync(original));
    }
}
