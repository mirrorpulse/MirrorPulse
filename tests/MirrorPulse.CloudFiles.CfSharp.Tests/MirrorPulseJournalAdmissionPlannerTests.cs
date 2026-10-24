using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalAdmissionPlannerTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task EveryTurnBoundsBookkeepingAndWithholdsCommandsUntilAllDependenciesAndProjectionFinish(int budget)
    {
        var rows = Rows(); var original = Plan(rows);
        var blockedRows = new CountedList<MirrorPulseBlockedLocalOperation>(original.BlockedOperations!);
        var heldRows = new CountedList<MirrorPulseWorkerChangeCommand>(original.RoutedBlockedCommands);
        var commandRows = new CountedList<MirrorPulseWorkerChangeCommand>(original.Commands);
        var completeRows = new CountedList<MirrorPulseWorkerChangeCommand>(original.CompleteWindow!);
        var plan = original with { BlockedOperations = blockedRows, RoutedBlockedCommands = heldRows, Commands = commandRows, CompleteWindow = completeRows };
        var admission = new MirrorPulseJournalAdmissionPlanner(plan, 6, budget);
        var recorded = new List<Guid>(); var inspected = new List<Guid>(); int turns = 0;
        while (true)
        {
            int before = recorded.Count + inspected.Count;
            int elementsBefore = blockedRows.ElementReads + heldRows.ElementReads + commandRows.ElementReads + completeRows.ElementReads;
            bool completed = await admission.AdvanceAsync((blocked, _) =>
            {
                recorded.Add(blocked.OperationId); return ValueTask.FromResult(blocked.OperationId == rows[0].OperationId);
            }, (command, _) =>
            {
                inspected.Add(command.OperationId); return ValueTask.FromResult(command.RootKey != "other");
            }, default);
            Assert.IsLessThanOrEqualTo(budget, recorded.Count + inspected.Count - before);
            Assert.IsLessThanOrEqualTo(budget,
                blockedRows.ElementReads + heldRows.ElementReads + commandRows.ElementReads + completeRows.ElementReads - elementsBefore);
            turns++;
            if (completed) break;
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = admission.Result);
            Assert.IsLessThan(30, turns);
        }
        Assert.IsGreaterThan(1, turns);
        CollectionAssert.AreEqual(new[] { rows[0].OperationId, rows[1].OperationId }, recorded);
        CollectionAssert.AreEqual(rows.Skip(2).Select(row => row.OperationId).ToArray(), inspected);
        var result = admission.Result;
        CollectionAssert.AreEqual(new[] { rows[3].OperationId, rows[5].OperationId }, result.ReadyCommands.Select(command => command.OperationId).ToArray());
        CollectionAssert.AreEqual(rows.Skip(1).Select(row => row.OperationId).ToArray(), result.CompleteWindow!.Select(command => command.OperationId).ToArray());
        Assert.AreEqual(4, result.DeferredCount); Assert.AreEqual(6L, result.WindowUpperSequence);
        Assert.AreSame(result.ReadyCommands, result.ValidatedSequenceCommands);
        Assert.IsFalse(result.AdmissionInProgress); Assert.IsFalse(result.RequiresFullRescan);
        Assert.IsTrue(await admission.AdvanceAsync((_, _) => throw new AssertFailedException("Bookkeeping must not run again."),
            (_, _) => throw new AssertFailedException("A completed batch must not acquire another request."), default));
        Assert.AreSame(result, admission.Result);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnchangedCompleteWindowIsSharedAndAnUnroutableWindowNeverBecomesComplete(bool unroutable)
    {
        var rows = Rows().Skip(2).ToArray();
        if (unroutable) rows[0] = rows[0] with { RelativePath = "Unknown/original.bin" };
        var plan = Plan(rows); var admission = new MirrorPulseJournalAdmissionPlanner(plan, 6, 1);
        while (!await admission.AdvanceAsync((_, _) => ValueTask.FromResult(false), (_, _) => ValueTask.FromResult(true), default)) { }
        if (unroutable) Assert.IsNull(admission.Result.CompleteWindow);
        else Assert.AreSame(plan.CompleteWindow, admission.Result.CompleteWindow);
    }

    [TestMethod]
    [DataRow("blocked")]
    [DataRow("request")]
    [DataRow("cancellation")]
    public async Task FailedAdmissionNeverPublishesAPartialBatchOrResumesAnUncertainCallback(string boundary)
    {
        var plan = Plan(Rows()); var admission = new MirrorPulseJournalAdmissionPlanner(plan, 6, 1);
        using var cancellation = new CancellationTokenSource();
        if (boundary == "cancellation") cancellation.Cancel();
        var original = new IOException("The original durable boundary failed.");
        async Task Advance()
        {
            while (!await admission.AdvanceAsync((_, _) => boundary == "blocked" ? throw original : ValueTask.FromResult(false),
                (_, _) => throw original, cancellation.Token)) { }
        }
        if (boundary == "cancellation") await Assert.ThrowsExactlyAsync<OperationCanceledException>(Advance);
        else Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(Advance));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = admission.Result);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await admission.AdvanceAsync(
            (_, _) => throw new AssertFailedException("An uncertain callback must not run twice."),
            (_, _) => throw new AssertFailedException("The failed owner must restart its official window."), default));
        var restarted = new MirrorPulseJournalAdmissionPlanner(plan, 6, 1);
        while (!await restarted.AdvanceAsync((_, _) => ValueTask.FromResult(false), (_, _) => ValueTask.FromResult(true), default)) { }
        CollectionAssert.AreEqual(new[] { plan.Commands[1].OperationId, plan.Commands[2].OperationId, plan.Commands[3].OperationId },
            restarted.Result.ReadyCommands.Select(command => command.OperationId).ToArray());
    }

    [TestMethod]
    public async Task OrdinaryBlockedOperationsCannotBeAcknowledgedAsMetadata()
    {
        var rows = Rows(); rows[0] = rows[0] with { Kind = CloudLocalChangeKind.Delete, RelativePath = "Docs", IsDirectory = true };
        var admission = new MirrorPulseJournalAdmissionPlanner(Plan(rows), 6, 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await admission.AdvanceAsync(
            (_, _) => ValueTask.FromResult(true), (_, _) => ValueTask.FromResult(true), default));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = admission.Result);
    }

    [TestMethod]
    public async Task ConcurrentAdvancementDoesNotStealOrPoisonTheOriginalAdmissionOwner()
    {
        var admission = new MirrorPulseJournalAdmissionPlanner(Plan(Rows()), 6, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = admission.AdvanceAsync(async (_, _) => { entered.SetResult(); await released.Task; return false; },
            (_, _) => ValueTask.FromResult(true), default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await admission.AdvanceAsync(
            (_, _) => ValueTask.FromResult(false), (_, _) => ValueTask.FromResult(true), default));
        }
        finally { released.SetResult(); }
        Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        while (!await admission.AdvanceAsync((_, _) => ValueTask.FromResult(false), (_, _) => ValueTask.FromResult(true), default)) { }
        Assert.HasCount(3, admission.Result.ReadyCommands);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4097)]
    public void InvalidAdmissionBudgetCannotAcquireAnOwner(int budget) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MirrorPulseJournalAdmissionPlanner(Plan(Rows()), 6, budget));

    [TestMethod]
    [DataRow(0L)]
    [DataRow(7L)]
    public async Task InvalidCommandSequenceCannotAcquireItsDurableRequest(long sequence)
    {
        var rows = Rows().Skip(2).ToArray(); rows[0] = rows[0] with { Sequence = sequence };
        var admission = new MirrorPulseJournalAdmissionPlanner(Plan(rows), 6, 2);
        int requests = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await admission.AdvanceAsync(
            (_, _) => ValueTask.FromResult(false), (_, _) => { requests++; return ValueTask.FromResult(true); }, default));
        Assert.AreEqual(0, requests);
    }

    private sealed class CountedList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        internal int ElementReads { get; private set; }
        public int Count => values.Count;
        public T this[int index] { get { ElementReads++; return values[index]; } }
        public IEnumerator<T> GetEnumerator() { for (int index = 0; index < Count; index++) yield return this[index]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static MirrorPulseLocalChangeObservation[] Rows() => Enumerable.Range(1, 6).Select(value =>
        new MirrorPulseLocalChangeObservation(Guid.NewGuid(), value,
            value <= 2 ? CloudLocalChangeKind.MetadataUpdate : CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(),
            value switch { 1 => "Docs/accepted", 2 => "Docs/held", 3 => "Docs/held/child.bin", 5 => "Other/independent.bin", _ => $"Docs/file-{value}.bin" },
            null, value <= 2, DateTimeOffset.UtcNow)).ToArray();

    private static MirrorPulseLocalBatchPlan Plan(MirrorPulseLocalChangeObservation[] rows)
    {
        var instance = InstanceId.New(); var adapter = AdapterId.Parse("example.admission");
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), "MirrorPulse-admission-tests", Guid.NewGuid().ToString("N")),
            [AdapterRootRegistrationMapper.Map(adapter, instance, new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot),
             AdapterRootRegistrationMapper.Map(adapter, instance, new("other", "Other", "Other", false), RootRegistrationState.Disabled, identityScope: RootIdentityScope.InstanceRoot)]);
        return MirrorPulseLocalBatchMapper.MapObservations(rows, router);
    }
}
