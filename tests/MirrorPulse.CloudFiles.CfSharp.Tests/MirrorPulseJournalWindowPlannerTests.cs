using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalWindowPlannerTests
{
    [TestMethod]
    public async Task EmptyTerminalWindowsFinishWithoutInvokingClassificationOrInventingPendingWork()
    {
        var planner = new MirrorPulseJournalWindowPlanner(Array.Empty<MirrorPulseLocalChangeObservation>(), Router(), 0, 1);
        Assert.IsTrue(await planner.AdvanceAsync((_, _) => throw new AssertFailedException("An empty window has no original observation."), default));
        Assert.IsEmpty(planner.Plan.Commands); Assert.IsEmpty(planner.Plan.CompleteWindow!);
        Assert.IsEmpty(planner.Plan.RoutedBlockedCommands); Assert.AreEqual(0L, planner.UpperSequence);
    }

    [TestMethod]
    public void FinalDependencyProjectionRejectsLaterObservationsAndPreservesAlreadyDeliveredLists()
    {
        var rows = Rows(2); var builder = new MirrorPulseLocalBatchPlanBuilder(Router());
        builder.Add(rows[0]); builder.Add(rows[1]);
        Assert.IsFalse(builder.Finish(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.Add(Rows(1)[0]));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = builder.Result);
        Assert.IsTrue(builder.Finish(1)); var delivered = builder.Result;
        Assert.IsTrue(builder.Finish(1)); Assert.AreSame(delivered, builder.Result);
        CollectionAssert.AreEqual(rows.Select(row => row.OperationId).ToArray(), delivered.CompleteWindow!.Select(row => row.OperationId).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task EveryTurnBoundsOriginalClassificationAndWithholdsTheWholePlanUntilFinalProjection(int budget)
    {
        var rows = Rows(7); var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 7, budget);
        var classified = new List<Guid>(); int turns = 0;
        while (true)
        {
            int before = classified.Count;
            bool complete = await planner.AdvanceAsync((row, _) => { classified.Add(row.OperationId); return ValueTask.FromResult(true); }, default);
            Assert.IsLessThanOrEqualTo(budget, classified.Count - before); turns++;
            if (complete) break;
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = planner.Plan);
            Assert.IsLessThan(30, turns);
        }
        CollectionAssert.AreEqual(rows.Select(row => row.OperationId).ToArray(), classified.ToArray());
        CollectionAssert.AreEqual(rows.Select(row => row.OperationId).ToArray(), planner.Plan.Commands.Select(row => row.OperationId).ToArray());
        Assert.HasCount(7, planner.Plan.CompleteWindow!); Assert.AreEqual(7L, planner.UpperSequence);
        Assert.IsGreaterThan(1, turns);
    }

    [TestMethod]
    public async Task SmallWindowsKeepTheOriginalSingleReadBehaviorAndCompletedResultsAreImmutable()
    {
        var rows = Rows(2); var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 2);
        Assert.IsTrue(await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default));
        var result = planner.Plan;
        Assert.IsTrue(await planner.AdvanceAsync((_, _) => throw new AssertFailedException("Completed history must not reclassify."), default));
        Assert.AreSame(result, planner.Plan); Assert.HasCount(2, result.Commands);
    }

    [TestMethod]
    public async Task RetiredOriginalsSettledDuringClassificationCannotReenterOrdinaryMapping()
    {
        var rows = Rows(4); var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 4, 1);
        var settled = new HashSet<Guid> { rows[1].OperationId, rows[3].OperationId };
        while (!await planner.AdvanceAsync((row, _) => ValueTask.FromResult(!settled.Contains(row.OperationId)), default)) { }
        CollectionAssert.AreEqual(new[] { rows[0].OperationId, rows[2].OperationId }, planner.Plan.Commands.Select(row => row.OperationId).ToArray());
        Assert.HasCount(2, planner.Plan.CompleteWindow!); Assert.IsEmpty(planner.Plan.BlockedOperations!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrClassificationFailurePoisonsTheWindowAndRequiresOfficialJournalReplay(bool cancel)
    {
        var rows = Rows(4); var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 4, 1);
        Assert.IsFalse(await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default));
        using var timeout = new CancellationTokenSource(); if (cancel) timeout.Cancel();
        if (cancel)
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), timeout.Token));
        else
            await Assert.ThrowsExactlyAsync<IOException>(async () => await planner.AdvanceAsync((_, _) => throw new IOException("Synthetic retained classification interruption."), default));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = planner.Plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default));
        var reopened = new MirrorPulseJournalWindowPlanner(rows, Router(), 4, 8); var replayed = new List<Guid>();
        Assert.IsTrue(await reopened.AdvanceAsync((row, _) => { replayed.Add(row.OperationId); return ValueTask.FromResult(true); }, default));
        CollectionAssert.AreEqual(rows.Select(row => row.OperationId).ToArray(), replayed.ToArray());
    }

    [TestMethod]
    public async Task RoutingFailuresAndCrossRootBlockedOriginalsRemainVisibleWithoutInventingACompleteCoalescingWindow()
    {
        var rows = Rows(5); rows[0] = rows[0] with { Kind = CloudLocalChangeKind.Delete, RelativePath = "Docs", IsDirectory = true };
        rows[1] = rows[1] with { Kind = CloudLocalChangeKind.MetadataUpdate };
        rows[2] = rows[2] with { Kind = CloudLocalChangeKind.Move, PreviousRelativePath = "Other/old.bin" };
        rows[3] = rows[3] with { RelativePath = "Unknown/original.bin" };
        var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 5, 1);
        while (!await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default)) { }
        Assert.IsNull(planner.Plan.CompleteWindow);
        CollectionAssert.AreEqual(new[] { rows[4].OperationId }, planner.Plan.Commands.Select(row => row.OperationId).ToArray());
        CollectionAssert.AreEqual(new[] { rows[0].OperationId, rows[1].OperationId, rows[2].OperationId },
            planner.Plan.RoutedBlockedCommands.Select(row => row.OperationId).ToArray());
        CollectionAssert.AreEqual(new[] { MirrorPulseLocalOperationBlockReason.RootReconciliationRequired,
            MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, MirrorPulseLocalOperationBlockReason.CrossRootMove,
            MirrorPulseLocalOperationBlockReason.UnregisteredPath }, planner.Plan.BlockedOperations!.Select(row => row.Reason).ToArray());
    }

    [TestMethod]
    public async Task ARepeatedBlockedOperationIdRetainsAllOfItsOriginalDependenciesAcrossTurns()
    {
        var rows = Rows(3); rows[2] = rows[2] with { OperationId = rows[0].OperationId, Kind = CloudLocalChangeKind.MetadataUpdate };
        var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 3, 1);
        while (!await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default)) { }
        CollectionAssert.AreEqual(new[] { rows[0].OperationId, rows[2].OperationId }, planner.Plan.RoutedBlockedCommands.Select(row => row.OperationId).ToArray());
        Assert.HasCount(3, planner.Plan.CompleteWindow!);
    }

    [TestMethod]
    public async Task ConcurrentAdvanceDoesNotResetOrPoisonTheOriginalOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new MirrorPulseJournalWindowPlanner(Rows(1), Router(), 1, 2);
        var first = planner.AdvanceAsync(async (_, _) => { entered.SetResult(); await released.Task; return true; }, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await planner.AdvanceAsync((_, _) => ValueTask.FromResult(true), default)); }
        finally { released.SetResult(); }
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(5))); Assert.HasCount(1, planner.Plan.Commands);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task OriginalSequenceOrderSurvivesHolesRetiredSettlementAndBlockedRoutes(int budget)
    {
        var rows = Rows(6);
        for (int index = 0; index < rows.Length; index++) rows[index] = rows[index] with { Sequence = (index + 1) * 11L };
        rows[2] = rows[2] with { RelativePath = "Other/disabled.bin" };
        rows[3] = rows[3] with { Kind = CloudLocalChangeKind.MetadataUpdate };
        var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 77, budget);
        var classified = new List<long>();
        while (!await planner.AdvanceAsync((row, _) =>
        {
            classified.Add(row.Sequence);
            return ValueTask.FromResult(row.OperationId != rows[1].OperationId);
        }, default)) { }
        CollectionAssert.AreEqual(rows.Select(row => row.Sequence).ToArray(), classified.ToArray());
        CollectionAssert.AreEqual(new[] { rows[0].OperationId, rows[2].OperationId, rows[4].OperationId, rows[5].OperationId },
            planner.Plan.Commands.Select(command => command.OperationId).ToArray());
        CollectionAssert.AreEqual(new long[] { 11, 33, 55, 66 }, planner.Plan.Commands.Select(command => command.Sequence).ToArray());
        CollectionAssert.AreEqual(new[] { rows[3].OperationId }, planner.Plan.RoutedBlockedCommands.Select(command => command.OperationId).ToArray());
        CollectionAssert.AreEqual(new long[] { 11, 33, 44, 55, 66 }, planner.Plan.CompleteWindow!.Select(command => command.Sequence).ToArray());
        Assert.AreEqual(77L, planner.UpperSequence);
    }

    [TestMethod]
    [DataRow(0L, 3)]
    [DataRow(-1L, 3)]
    [DataRow(1L, 3)]
    [DataRow(4L, 3)]
    [DataRow(1L, 1)]
    [DataRow(4L, 1)]
    public async Task InvalidOriginalSequenceNeverReachesClassificationOrExposesAPartialPlan(long invalid, int budget)
    {
        var rows = Rows(2); rows[1] = rows[1] with { Sequence = invalid };
        var planner = new MirrorPulseJournalWindowPlanner(rows, Router(), 3, budget);
        var classified = new List<Guid>();
        async Task AdvanceUntilComplete()
        {
            while (!await planner.AdvanceAsync((row, _) =>
            {
                classified.Add(row.OperationId);
                // A settled original still participates in the sequence fence.
                return ValueTask.FromResult(false);
            }, default)) { }
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(AdvanceUntilComplete);
        CollectionAssert.AreEqual(new[] { rows[0].OperationId }, classified.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = planner.Plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await planner.AdvanceAsync((_, _) => throw new AssertFailedException("A failed window must not classify another observation."), default));
    }

    [TestMethod]
    public void NegativeFiniteBoundaryCannotCreateAPlanningOwner() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MirrorPulseJournalWindowPlanner([], Router(), -1));

    [TestMethod]
    [DataRow(0)]
    [DataRow(4097)]
    public void InvalidObservationBudgetCannotCreateAPlanningOwner(int budget) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MirrorPulseJournalWindowPlanner(Rows(1), Router(), 1, budget));

    private static MirrorPulseLocalChangeObservation[] Rows(int count) => Enumerable.Range(1, count).Select(value =>
        new MirrorPulseLocalChangeObservation(Guid.NewGuid(), value, CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(),
            "Docs/original.bin", null, false, DateTimeOffset.UtcNow)).ToArray();

    private static MirrorPulseRootRouter Router()
    {
        var instance = InstanceId.New(); var adapter = AdapterId.Parse("example.window");
        return new(Path.Combine(Path.GetTempPath(), "MirrorPulse-planner-tests", Guid.NewGuid().ToString("N")),
            [AdapterRootRegistrationMapper.Map(adapter, instance, new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot),
             AdapterRootRegistrationMapper.Map(adapter, instance, new("other", "Other", "Other", false), RootRegistrationState.Disabled, identityScope: RootIdentityScope.InstanceRoot)]);
    }
}
