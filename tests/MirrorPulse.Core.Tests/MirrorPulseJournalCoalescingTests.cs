using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalCoalescingTests
{
    [TestMethod]
    public void RenameLoopRequiresRemoteVerificationAndIncompleteOrCrossRootChainsCannotMerge()
    {
        MirrorPulseWorkerChangeCommand[] commands = Chain(MirrorPulseWorkerChangeKind.Move, false);
        commands[1] = commands[1] with { RelativePath = "original.txt" };
        Guid item = commands[0].ItemId!.Value;
        Assert.AreEqual(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged,
            MirrorPulseJournalCoalescingPlanner.TryPlan(commands, item, 2, new HashSet<Guid>())!.Effect);
        Assert.ThrowsExactly<ArgumentException>(() => MirrorPulseJournalCoalescingPlanner.TryPlan(commands, item, 1, new HashSet<Guid>()));
        commands[1] = commands[1] with { RootKey = "other-root" };
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan(commands, item, 2, new HashSet<Guid>()));
        commands[1] = commands[1] with { RootKey = "files", IsDirectory = true };
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan(commands, item, 2, new HashSet<Guid>()));
    }

    [TestMethod]
    public void RepeatedContentEditsBecomeOneUpdateWhileDeleteAndRecreationRemainSeparate()
    {
        MirrorPulseWorkerChangeCommand[] chain = Chain(MirrorPulseWorkerChangeKind.ContentUpdate, false);
        MirrorPulseWorkerChangeCommand second = chain[2] with { Sequence = 2, RelativePath = "original.txt" };
        Guid item = chain[0].ItemId!.Value;
        Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile,
            MirrorPulseJournalCoalescingPlanner.TryPlan([chain[0], second], item, 2, new HashSet<Guid>())!.Effect);
        MirrorPulseWorkerChangeCommand deletion = chain[0] with { Kind = MirrorPulseWorkerChangeKind.Delete };
        MirrorPulseWorkerChangeCommand replacement = second with { Kind = MirrorPulseWorkerChangeKind.Create };
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan([deletion, replacement], item, 2, new HashSet<Guid>()));
    }

    [TestMethod]
    [DataRow(MirrorPulseWorkerChangeKind.Create, false, MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseWorkerChangeKind.Create, true, MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseWorkerChangeKind.ContentUpdate, false, MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseWorkerChangeKind.ContentUpdate, true, MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseWorkerChangeKind.Move, false, MirrorPulseCoalescedEffect.MoveFile)]
    public void FileChainKeepsOriginalPathAndFinalNameWithoutLosingAcknowledgementIdentities(
        MirrorPulseWorkerChangeKind firstKind, bool deleted, MirrorPulseCoalescedEffect expected)
    {
        MirrorPulseWorkerChangeCommand[] commands = Chain(firstKind, deleted);
        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(commands, commands[0].ItemId!.Value, 100, new HashSet<Guid>())!;
        Assert.AreEqual(expected, plan.Effect);
        Assert.AreEqual("original.txt", plan.OriginalPath);
        Assert.AreEqual("final.txt", plan.FinalPath);
        CollectionAssert.AreEqual(commands.Select(command => command.OperationId).ToArray(), plan.Members.Select(member => member.OperationId).ToArray());
        Assert.AreEqual(plan.PlanId, MirrorPulseJournalCoalescingPlanner.TryPlan(commands.Reverse().ToArray(), commands[0].ItemId!.Value, 100, new HashSet<Guid>())!.PlanId);
        Assert.AreNotEqual(plan.PlanId, commands[0].OperationId);
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan(commands, commands[0].ItemId!.Value, 100, new HashSet<Guid> { commands[1].OperationId }));
        var collision = commands[0] with { OperationId = Guid.NewGuid(), ItemId = Guid.NewGuid(), Sequence = 90 };
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan([.. commands, collision], commands[0].ItemId!.Value, 100, new HashSet<Guid>()));
        var independent = collision with { RelativePath = "unrelated.txt", Kind = MirrorPulseWorkerChangeKind.ContentUpdate, PreviousRelativePath = null };
        Assert.IsNotNull(MirrorPulseJournalCoalescingPlanner.TryPlan([.. commands, independent], commands[0].ItemId!.Value, 100, new HashSet<Guid>()));
    }

    [TestMethod]
    public async Task ImmutablePlanRetainsMembersAcrossRestartAndFencesTheirIndividualExecution()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-coalescing", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        MirrorPulseWorkerChangeCommand[] commands = Chain(MirrorPulseWorkerChangeKind.Create, false);
        MirrorPulseJournalCoalescingPlan plan = MirrorPulseJournalCoalescingPlanner.TryPlan(commands, commands[0].ItemId!.Value, 100, new HashSet<Guid>())!;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.PrepareJournalCoalescingAsync(plan);
                await catalog.PrepareJournalCoalescingAsync(plan);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingAsync(plan with { FinalPath = "different.txt" }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(plan with { PlanId = Guid.NewGuid() }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(Intent(commands[0])));
                Assert.IsNull(await catalog.ReadMutationAsync(commands[0].OperationId));
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseJournalCoalescingPlan restored = (await reopened.ReadJournalCoalescingPlansAsync()).Single();
            Assert.AreEqual(plan.PlanId, restored.PlanId);
            Assert.AreEqual(plan.Effect, restored.Effect);
            Assert.AreEqual(plan.FinalPath, restored.FinalPath);
            CollectionAssert.AreEqual(plan.Members.ToArray(), restored.Members.ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareMutationAsync(Intent(commands[^1])));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Acknowledged)]
    [DataRow(MirrorPulseMutationState.Conflict)]
    public async Task ExistingMutationAtAnyBoundaryPreventsMergeWithoutPartialMemberReservation(MirrorPulseMutationState state)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-coalescing-fence", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        MirrorPulseWorkerChangeCommand[] commands = Chain(MirrorPulseWorkerChangeKind.Create, false);
        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(commands, commands[0].ItemId!.Value, 100, new HashSet<Guid>())!;
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            Guid operation = commands[1].OperationId;
            await catalog.PrepareMutationAsync(Intent(commands[1]));
            if (state != MirrorPulseMutationState.Prepared)
            {
                await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
                if (state == MirrorPulseMutationState.Ambiguous)
                    await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Ambiguous);
                if (state == MirrorPulseMutationState.Conflict)
                    await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
                if (state is MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Acknowledged)
                    await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "v1");
                if (state == MirrorPulseMutationState.Acknowledged)
                    await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(plan));
            Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
            Assert.AreEqual(state, (await catalog.ReadMutationAsync(operation))!.State);
            // A rejected plan must not reserve an earlier member before encountering the fence.
            await catalog.PrepareMutationAsync(Intent(commands[0]));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CompetingIndividualMutationAndPlanCannotBothOwnAnOriginalOperation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-coalescing-race", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        MirrorPulseWorkerChangeCommand[] commands = Chain(MirrorPulseWorkerChangeKind.Create, false);
        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(commands, commands[0].ItemId!.Value, 100, new HashSet<Guid>())!;
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            async Task<bool> OwnAsync(bool aggregate)
            {
                try
                {
                    if (aggregate) await catalog.PrepareJournalCoalescingAsync(plan);
                    else await catalog.PrepareMutationAsync(Intent(commands[0]));
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }
            bool[] result = await Task.WhenAll(Task.Run(() => OwnAsync(true)), Task.Run(() => OwnAsync(false)));
            Assert.AreEqual(1, result.Count(success => success));
            Assert.HasCount(result[0] ? 1 : 0, await catalog.ReadJournalCoalescingPlansAsync());
            Assert.AreEqual(result[1], await catalog.ReadMutationAsync(commands[0].OperationId) is not null);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static MirrorPulseMutationIntent Intent(MirrorPulseWorkerChangeCommand command) =>
        new(command.OperationId, command.InstanceId, command.RootKey, command.Kind, command.RelativePath,
            command.PreviousRelativePath, command.IsDirectory, null, null, null, MirrorPulseMutationOrigin.Journal);

    private static MirrorPulseWorkerChangeCommand[] Chain(MirrorPulseWorkerChangeKind firstKind, bool deleted)
    {
        InstanceId instance = InstanceId.New();
        Guid item = Guid.NewGuid();
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        MirrorPulseWorkerChangeCommand Command(long sequence, MirrorPulseWorkerChangeKind kind, string path, string? previous = null) =>
            new(Guid.NewGuid(), sequence, instance, "files", kind, path, previous is null ? null : "files", previous, false, item, observed.AddSeconds(sequence));
        var commands = new List<MirrorPulseWorkerChangeCommand>();
        if (firstKind == MirrorPulseWorkerChangeKind.Move)
            commands.Add(Command(1, firstKind, "intermediate.txt", "original.txt"));
        else commands.Add(Command(1, firstKind, "original.txt"));
        commands.Add(Command(2, MirrorPulseWorkerChangeKind.Move, "final.txt", firstKind == MirrorPulseWorkerChangeKind.Move ? "intermediate.txt" : "original.txt"));
        if (firstKind != MirrorPulseWorkerChangeKind.Move) commands.Add(Command(3, MirrorPulseWorkerChangeKind.ContentUpdate, "final.txt"));
        if (deleted) commands.Add(Command(4, MirrorPulseWorkerChangeKind.Delete, "final.txt"));
        return commands.ToArray();
    }
}
