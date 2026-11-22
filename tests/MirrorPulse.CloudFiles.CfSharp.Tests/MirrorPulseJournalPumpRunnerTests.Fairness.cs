using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseJournalPumpRunnerTests
{
    private static readonly string[] PersistentRootOrder = ["root-0", "root-1", "root-2", "root-3", "root-4", "root-5", "root-6", "root-0"];

    [TestMethod]
    public async Task BusyRootsCannotConsumeTheCycleOrPreventTheirPeersFromDispatching()
    {
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 3, maximumCommandsPerRoot: 2);
        InstanceId instance = InstanceId.New();
        var backlog = Enumerable.Range(0, 20).Select(index => FairCommand(instance, "busy", index + 1, $"file-{index}.txt")).ToList();
        MirrorPulseWorkerChangeCommand peer = FairCommand(instance, "peer", 100, "peer.txt");
        backlog.Add(peer);
        var called = new List<Guid>();
        var accepted = new HashSet<Guid>();
        bool progressed = await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(backlog, 0, false)),
            (command, _) =>
            {
                called.Add(command.OperationId);
                if (command == peer) accepted.Add(command.OperationId);
                return ValueTask.FromResult(command == peer);
            }, (_, _, _, _) => throw new AssertFailedException("A deferred command is not a dispatch fault."), CancellationToken.None);
        Assert.IsTrue(progressed);
        CollectionAssert.AreEqual(new[] { backlog[0].OperationId, peer.OperationId, backlog[1].OperationId }, called);
        CollectionAssert.AreEquivalent(new[] { peer.OperationId }, accepted.ToArray());
        Assert.IsTrue(runner.Health.Healthy);
        Assert.HasCount(21, backlog, "Budgeting must not remove or acknowledge deferred original IDs.");
    }

    [TestMethod]
    public async Task CursorRotatesAcrossMorePersistentRootsThanTheGlobalBudget()
    {
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 2, maximumCommandsPerRoot: 1);
        InstanceId instance = InstanceId.New();
        MirrorPulseWorkerChangeCommand[] commands = Enumerable.Range(0, 7)
            .Select(index => FairCommand(instance, $"root-{index}", index + 1, "retained.txt")).ToArray();
        var visited = new List<string>();
        for (int cycle = 0; cycle < 4; cycle++)
        {
            int before = visited.Count;
            Assert.IsFalse(await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(commands, 0, false)),
                (command, _) => { visited.Add(command.RootKey); return ValueTask.FromResult(false); },
                (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
            Assert.AreEqual(2, visited.Count - before);
        }
        CollectionAssert.AreEqual(PersistentRootOrder, visited);
        Assert.IsTrue(runner.Health.Healthy);
    }

    [TestMethod]
    public async Task UnselectedOriginalParentStillFencesACrossRootMoveUntilAccepted()
    {
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 1, maximumCommandsPerRoot: 1);
        InstanceId instance = InstanceId.New();
        MirrorPulseWorkerChangeCommand independent = FairCommand(instance, "independent", 1, "other.txt");
        var parent = FairCommand(instance, "source", 2, "folder") with { IsDirectory = true };
        var move = FairCommand(instance, "target", 3, "renamed.txt") with
        {
            Kind = MirrorPulseWorkerChangeKind.Move,
            PreviousRootKey = "source",
            PreviousRelativePath = "folder/child.txt"
        };
        var pending = new List<MirrorPulseWorkerChangeCommand> { independent, parent, move };
        var calls = new List<Guid>();
        bool allowParent = false;
        ValueTask<MirrorPulseJournalUploadBatch> Read(CancellationToken _) => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(pending.ToArray(), 0, false));
        ValueTask<bool> Dispatch(MirrorPulseWorkerChangeCommand command, CancellationToken _)
        {
            calls.Add(command.OperationId);
            bool accepted = command != parent || allowParent;
            if (accepted) pending.Remove(command);
            return ValueTask.FromResult(accepted);
        }
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { independent.OperationId, parent.OperationId }, calls);
        Assert.Contains(move, pending);
        allowParent = true;
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { independent.OperationId, parent.OperationId, parent.OperationId, move.OperationId }, calls);
        Assert.IsEmpty(pending);
    }

    [TestMethod]
    public async Task EqualRootKeysInDifferentInstancesHaveSeparateBudgetsAndCancellationStopsAdmission()
    {
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 4, maximumCommandsPerRoot: 1);
        InstanceId first = InstanceId.New();
        MirrorPulseWorkerChangeCommand[] commands = [FairCommand(first, "same", 1, "one.txt"),
            FairCommand(first, "same", 2, "two.txt"), FairCommand(InstanceId.New(), "same", 3, "three.txt")];
        var accepted = new List<Guid>();
        await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(commands, 0, false)),
            (command, _) => { accepted.Add(command.OperationId); return ValueTask.FromResult(true); },
            (_, _, _, _) => ValueTask.CompletedTask, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { commands[0].OperationId, commands[2].OperationId }, accepted);
        using var cancellation = new CancellationTokenSource();
        accepted.Clear();
        runner = new(maximumCommandsPerCycle: 4, maximumCommandsPerRoot: 4);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.RunCycleAsync(
            _ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(commands, 0, false)),
            (command, _) => { accepted.Add(command.OperationId); cancellation.Cancel(); return ValueTask.FromResult(true); },
            (_, _, _, _) => ValueTask.CompletedTask, cancellation.Token).AsTask());
        CollectionAssert.AreEqual(new[] { commands[0].OperationId }, accepted);
    }

    private static MirrorPulseWorkerChangeCommand FairCommand(InstanceId instance, string root, long sequence, string path) =>
        new(Guid.NewGuid(), sequence, instance, root, MirrorPulseWorkerChangeKind.Create, path, null, null, false, null, DateTimeOffset.UtcNow);
}
