using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseJournalPumpRunnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacingReadyCommandsCannotReuseTheOriginalSequenceBinding(bool replace)
    {
        InstanceId instance = InstanceId.New();
        var first = FairCommand(instance, "docs", 1, "first.txt");
        var second = FairCommand(instance, "docs", 2, "second.txt");
        IReadOnlyList<MirrorPulseWorkerChangeCommand> original = Array.AsReadOnly(new[] { first, second });
        var batch = new MirrorPulseJournalUploadBatch(original, 0, false) { ValidatedSequenceCommands = original };
        if (replace) batch = batch with { ReadyCommands = Array.AsReadOnly(new[] { second, first }) };
        var calls = new List<Guid>();
        Assert.IsTrue(await new MirrorPulseJournalPumpRunner().RunCycleAsync(_ => ValueTask.FromResult(batch),
            (command, _) => { calls.Add(command.OperationId); return ValueTask.FromResult(true); },
            (_, _, _, _) => throw new AssertFailedException("Original ordering should not fail."), default));
        CollectionAssert.AreEqual(new[] { first.OperationId, second.OperationId }, calls);
        CollectionAssert.AreEqual(new[] { first, second }, original.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidatedOriginalOrderRetainsEveryUnselectedDependencyAndIndependentPeer(bool fault)
    {
        InstanceId instance = InstanceId.New();
        var parent = FairCommand(instance, "left", 1, "folder") with { IsDirectory = true };
        var move = FairCommand(instance, "right", 2, "renamed.txt") with
        { Kind = MirrorPulseWorkerChangeKind.Move, PreviousRootKey = "left", PreviousRelativePath = "folder/child.txt" };
        var peer = FairCommand(instance, "peer", 3, "independent.txt");
        IReadOnlyList<MirrorPulseWorkerChangeCommand> original = Array.AsReadOnly(new[] { parent, move, peer });
        var batch = new MirrorPulseJournalUploadBatch(original, 0, false) { ValidatedSequenceCommands = original };
        var calls = new List<Guid>(); var reported = new List<Guid>();
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 3, maximumCommandsPerRoot: 1);
        Assert.IsTrue(await runner.RunCycleAsync(_ => ValueTask.FromResult(batch), (command, _) =>
        {
            calls.Add(command.OperationId);
            if (command == parent && fault) throw new IOException("Original parent outcome unavailable.");
            return ValueTask.FromResult(command != parent);
        }, (command, _, _, _) => { reported.Add(command!.OperationId); return ValueTask.CompletedTask; }, default));
        CollectionAssert.AreEqual(new[] { parent.OperationId, peer.OperationId }, calls);
        CollectionAssert.AreEqual(fault ? new[] { parent.OperationId } : [], reported);
        Assert.AreEqual(!fault, runner.Health.Healthy);
        CollectionAssert.AreEqual(new[] { parent, move, peer }, original.ToArray());
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(3, 2)]
    [DataRow(64, 8)]
    public async Task ExhaustedRootsLeaveTheCurrentCycleWithoutSkippingBusyIndependentCommands(int cycleBudget, int rootBudget)
    {
        InstanceId instance = InstanceId.New();
        var commands = new List<MirrorPulseWorkerChangeCommand>();
        commands.AddRange(Enumerable.Range(1, 5).Select(index => FairCommand(instance, "busy", index, $"busy-{index}.txt")));
        commands.Add(FairCommand(instance, "once", 20, "once.txt"));
        commands.AddRange(Enumerable.Range(21, 5).Select(index => FairCommand(instance, "peer", index, $"peer-{index}.txt")));
        var pending = commands.ToList(); var calls = new List<Guid>();
        var runner = new MirrorPulseJournalPumpRunner(cycleBudget, rootBudget);
        int turns = 0;
        while (pending.Count > 0)
        {
            var round = new List<MirrorPulseWorkerChangeCommand>();
            IReadOnlyList<MirrorPulseWorkerChangeCommand> ready = Array.AsReadOnly(pending.ToArray());
            Assert.IsTrue(await runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(ready, 0, false)
            { ValidatedSequenceCommands = ready }), (command, _) =>
            {
                round.Add(command); calls.Add(command.OperationId); pending.Remove(command); return ValueTask.FromResult(true);
            }, (_, _, _, _) => throw new AssertFailedException("Independent original commands should dispatch."), default));
            Assert.IsLessThanOrEqualTo(cycleBudget, round.Count);
            foreach (var root in round.GroupBy(command => command.RootKey)) Assert.IsLessThanOrEqualTo(rootBudget, root.Count());
            Assert.IsLessThanOrEqualTo(commands.Count, ++turns);
        }
        CollectionAssert.AreEquivalent(commands.Select(command => command.OperationId).ToArray(), calls);
        Assert.HasCount(commands.Count, calls.Distinct());
    }

    [TestMethod]
    public async Task RemovingTheCursorRootAndAddingAnotherRootCannotStrandOriginalPeers()
    {
        InstanceId instance = InstanceId.New();
        var first = FairCommand(instance, "first", 1, "first.txt");
        var gone = FairCommand(instance, "gone", 2, "gone.txt");
        var peer = FairCommand(instance, "peer", 3, "peer.txt");
        var added = FairCommand(instance, "added", 4, "added.txt");
        MirrorPulseWorkerChangeCommand[] current = [first, gone, peer];
        var calls = new List<Guid>(); var runner = new MirrorPulseJournalPumpRunner(1, 1);
        ValueTask<MirrorPulseJournalUploadBatch> Read(CancellationToken _) => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(current, 0, false));
        ValueTask<bool> Dispatch(MirrorPulseWorkerChangeCommand command, CancellationToken _)
        { calls.Add(command.OperationId); return ValueTask.FromResult(false); }
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default));
        current = [peer, added];
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default));
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default));
        CollectionAssert.AreEqual(new[] { first.OperationId, peer.OperationId, added.OperationId }, calls);
    }
}
