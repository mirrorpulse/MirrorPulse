using System.Text.Json;
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
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    public async Task PreflightDeletionRetainsTheExactPublicObservationWithoutInventingAnOriginalMutation(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect);
        var observation = effect == MirrorPulseCoalescedEffect.DeleteFile ? fixture.Window[^1] : fixture.Window[0];
        Guid command;
        string retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var authorization = await SavePreflightAuthorizationAsync(catalog, fixture, observation); command = authorization.Command;
            var decision = await catalog.PrepareConflictDeletionFromObservationAsync(command, authorization.Conflict, observation.RootKey,
                observation.ItemId!.Value, observation.RelativePath, fixture.Content, observation);
            Assert.AreEqual(2, decision.Intent.Version); Assert.AreEqual(observation, decision.Intent.OriginalObservation);
            Assert.AreEqual(observation.Kind == MirrorPulseWorkerChangeKind.Move ? observation.PreviousRelativePath : observation.RelativePath,
                decision.Intent.RemoteDeletion.RelativePath);
            Assert.AreEqual("observed-preflight", decision.Intent.RemoteDeletion.ExpectedRevision);
            Assert.IsNull(await catalog.ReadMutationAsync(observation.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(decision.Intent.RemoteDeletion.OperationId))!.State);
            retained = JsonSerializer.Serialize(decision);
            Assert.AreEqual(retained, JsonSerializer.Serialize(await catalog.PrepareConflictDeletionFromObservationAsync(command, authorization.Conflict,
                observation.RootKey, observation.ItemId.Value, observation.RelativePath, fixture.Content, observation)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareConflictDeletionFromObservationAsync(command, authorization.Conflict,
                observation.RootKey, observation.ItemId.Value, observation.RelativePath, fixture.Content, observation with { ObservedAt = observation.ObservedAt.AddSeconds(1) }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(fixture.Intent(observation)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(fixture.Plan));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(retained, JsonSerializer.Serialize(await reopened.ReadConflictDeletionAsync(command)));
        Assert.IsNull(await reopened.ReadMutationAsync(observation.OperationId));
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    public async Task APreflightObservationCannotDiscardAnExistingUnknownOrAcceptedExecution(MirrorPulseMutationState state)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observation = fixture.Window[0]; var original = fixture.Intent(observation);
        await catalog.PrepareMutationAsync(original);
        if (state != MirrorPulseMutationState.Prepared)
        {
            await catalog.TransitionMutationAsync(original.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            if (state != MirrorPulseMutationState.Executing)
                await catalog.TransitionMutationAsync(original.OperationId, MirrorPulseMutationState.Executing, state);
        }
        var before = (await catalog.ReadMutationAsync(original.OperationId))!;
        var authorization = await SavePreflightAuthorizationAsync(catalog, fixture, observation);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareConflictDeletionFromObservationAsync(authorization.Command,
            authorization.Conflict, observation.RootKey, observation.ItemId!.Value, observation.RelativePath, fixture.Content, observation));
        Assert.AreEqual(before, await catalog.ReadMutationAsync(original.OperationId));
        Assert.IsEmpty(await catalog.ReadPendingConflictDeletionsAsync());
        Assert.IsNull(await catalog.ReadConflictDeletionOwnerAsync(original.OperationId));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("operation")]
    [DataRow("root")]
    [DataRow("sequence")]
    [DataRow("directory")]
    [DataRow("metadata")]
    public async Task AChangedPublicObservationCannotAuthorizeDeletion(string change)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = fixture.Window[0]; var authorization = await SavePreflightAuthorizationAsync(catalog, fixture, original);
        var observation = change switch
        {
            "item" => original with { ItemId = Guid.NewGuid() },
            "operation" => original with { OperationId = Guid.NewGuid() },
            "root" => original with { RootKey = "other-root" },
            "sequence" => original with { Sequence = 0 },
            "directory" => original with { IsDirectory = true },
            _ => original with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate },
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareConflictDeletionFromObservationAsync(authorization.Command,
            authorization.Conflict, original.RootKey, original.ItemId!.Value, original.RelativePath, fixture.Content, observation));
        Assert.IsEmpty(await catalog.ReadPendingConflictDeletionsAsync()); Assert.IsNull(await catalog.ReadMutationAsync(original.OperationId));
    }

    private static async Task<(Guid Command, Guid Conflict)> SavePreflightAuthorizationAsync(MirrorPulseProductCatalog catalog,
        Fixture fixture, MirrorPulseWorkerChangeCommand observation)
    {
        Guid command = Guid.NewGuid(), conflict = Guid.NewGuid();
        await catalog.SaveUploadConflictAsync(new(conflict, observation.InstanceId, observation.OperationId.ToString("D"), observation.RelativePath,
            MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, fixture.Revision, "observed-preflight", DateTimeOffset.UtcNow));
        await catalog.SaveUserCommandAsync(new(command, "deleteremote", conflict.ToString("D"), "pending"));
        return (command, conflict);
    }
}
