using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseCrossInstanceJournalRoutingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HeldCrossInstanceMoveRetainsDependenciesOnItsOriginalSourceRoute(bool directory)
    {
        InstanceId source = InstanceId.New(), target = InstanceId.New();
        var sourceRoot = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.source"), source,
            new("same", "Source", "Source", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var targetRoot = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.target"), target,
            new("same", "Target", "Target", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), [sourceRoot, targetRoot]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var change = new MirrorPulseLocalChangeObservation(Guid.NewGuid(), 2, CloudLocalChangeKind.Move, Guid.NewGuid(),
            "Target/renamed", "Source/original", directory, now);
        var plan = MirrorPulseLocalBatchMapper.MapObservations([change], router);
        Assert.IsEmpty(plan.Commands); Assert.AreEqual(MirrorPulseLocalOperationBlockReason.CrossRootMove, plan.BlockedOperations!.Single().Reason);
        var retained = plan.CompleteWindow!.Single();
        Assert.AreEqual(source, retained.PreviousInstanceId); Assert.AreEqual(target, retained.InstanceId);
        Assert.AreEqual(retained, plan.RoutedBlockedCommands.Single());
        string path = directory ? "original/child.txt" : "original";
        var earlier = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, source, "same", MirrorPulseWorkerChangeKind.ContentUpdate,
            path, null, null, false, Guid.NewGuid(), now);
        var later = earlier with { OperationId = Guid.NewGuid(), Sequence = 3 };
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(retained, earlier), "The source-side predecessor remains part of a held transfer.");
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(later, retained), "A later source-side edit cannot bypass the held transfer.");
        Assert.IsFalse(MirrorPulseJournalOrderPolicy.DependsOn(later with { InstanceId = InstanceId.New() }, retained),
            "Equal root keys and paths in an unrelated instance are separate objects.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RoutableBlockedNamesRemainHeldWhenAnotherObservationMakesTheWindowIncomplete(bool incomplete)
    {
        InstanceId instance = InstanceId.New();
        var first = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.held"), instance,
            new("first", "First", "First", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var second = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.held"), instance,
            new("second", "Second", "Second", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), [first, second]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MirrorPulseLocalChangeObservation[] observations = [
            new(Guid.NewGuid(), 1, CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(), "First/original/child.txt", null, false, now),
            new(Guid.NewGuid(), 2, CloudLocalChangeKind.Move, Guid.NewGuid(), "Second/renamed", "First/original", true, now),
            new(Guid.NewGuid(), 3, CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(), "First/original/child.txt", null, false, now),
            new(Guid.NewGuid(), 4, CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(), "Second/renamed/child.txt", null, false, now),
            new(Guid.NewGuid(), 5, CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(), "First/independent.txt", null, false, now)
        ];
        var plan = MirrorPulseLocalBatchMapper.MapObservations(incomplete ? [.. observations,
            new(Guid.NewGuid(), 6, CloudLocalChangeKind.Create, Guid.NewGuid(), "Unknown/name.txt", null, false, now)] : observations, router);
        Assert.AreEqual(incomplete, plan.CompleteWindow is null);
        var held = plan.RoutedBlockedCommands.Single(); Assert.AreEqual(observations[1].OperationId, held.OperationId);
        Assert.IsNull(held.PreviousInstanceId, "Same-instance legacy serialization keeps its absent optional field.");
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(plan.Commands[0], held));
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(plan.Commands[1], held));
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(plan.Commands[2], held));
        Assert.IsFalse(MirrorPulseJournalOrderPolicy.DependsOn(plan.Commands[3], held));
        Assert.AreEqual(1L, plan.Commands[0].Sequence, "An earlier source observation must retain its journal order before the transfer.");
        Assert.AreEqual(3L, plan.Commands[1].Sequence);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForeignMoveAndLaterSourceWorkWaitForTheirActualPredecessorsWhileAnIndependentInstanceProgresses(bool directory)
    {
        InstanceId source = InstanceId.New(), target = InstanceId.New(); DateTimeOffset now = DateTimeOffset.UtcNow;
        string sourcePath = directory ? "original/child.txt" : "original";
        var first = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, source, "same", MirrorPulseWorkerChangeKind.ContentUpdate,
            sourcePath, null, null, false, Guid.NewGuid(), now);
        var move = first with
        {
            OperationId = Guid.NewGuid(),
            Sequence = 2,
            InstanceId = target,
            Kind = MirrorPulseWorkerChangeKind.Move,
            RelativePath = "renamed",
            PreviousInstanceId = source,
            PreviousRootKey = "same",
            PreviousRelativePath = "original",
            IsDirectory = directory
        };
        var after = first with { OperationId = Guid.NewGuid(), Sequence = 3 };
        var independent = first with { OperationId = Guid.NewGuid(), Sequence = 4, InstanceId = InstanceId.New() };
        var pending = new List<MirrorPulseWorkerChangeCommand> { first, move, after, independent };
        var runner = new MirrorPulseJournalPumpRunner(maximumCommandsPerCycle: 8, maximumCommandsPerRoot: 8);
        var calls = new List<Guid>(); bool allowFirst = false, allowMove = false;
        ValueTask<MirrorPulseJournalUploadBatch> Read(CancellationToken _) => ValueTask.FromResult(new MirrorPulseJournalUploadBatch(pending.ToArray(), 0, false));
        ValueTask<bool> Dispatch(MirrorPulseWorkerChangeCommand command, CancellationToken _)
        {
            if (command == move) Assert.DoesNotContain(first, pending, "The source predecessor has to be accepted before transfer dispatch.");
            if (command == after) Assert.DoesNotContain(move, pending, "A source successor cannot pass the unaccepted transfer.");
            calls.Add(command.OperationId);
            bool accepted = command == first ? allowFirst : command != move || allowMove;
            if (accepted) pending.Remove(command);
            return ValueTask.FromResult(accepted);
        }
        Assert.IsTrue(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default));
        CollectionAssert.AreEquivalent(new[] { first.OperationId, independent.OperationId }, calls);
        calls.Clear(); allowFirst = true;
        for (int cycle = 0; cycle < 4 && !calls.Contains(move.OperationId); cycle++)
            await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default);
        Assert.DoesNotContain(first, pending); Assert.Contains(move.OperationId, calls); Assert.DoesNotContain(after.OperationId, calls);
        Assert.Contains(move, pending); Assert.Contains(after, pending); calls.Clear();
        Assert.IsFalse(await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default));
        CollectionAssert.AreEqual(new[] { move.OperationId }, calls);
        calls.Clear(); allowMove = true;
        for (int cycle = 0; cycle < 4 && pending.Count != 0; cycle++)
            await runner.RunCycleAsync(Read, Dispatch, (_, _, _, _) => ValueTask.CompletedTask, default);
        CollectionAssert.AreEqual(new[] { move.OperationId, after.OperationId }, calls);
        Assert.IsEmpty(pending); Assert.IsTrue(runner.Health.Healthy);
    }
}
