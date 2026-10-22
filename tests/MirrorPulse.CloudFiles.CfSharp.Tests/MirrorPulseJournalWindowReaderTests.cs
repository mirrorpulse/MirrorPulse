using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalWindowReaderTests
{
    [TestMethod]
    [DataRow(0, 2)]
    [DataRow(65, 2)]
    [DataRow(1, 0)]
    [DataRow(1, 4097)]
    public void InvalidBudgetsCannotBeginReadingTheOfficialJournal(int pages, int size)
    {
        var journal = new Journal(5);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.Reader(pages, size));
        Assert.AreEqual(0, journal.Begins); Assert.IsEmpty(journal.Calls);
    }

    [TestMethod]
    public async Task BoundedTurnsExposeOnlyTheCompleteOriginalWindowAndKeepLaterEnqueuesOutsideIt()
    {
        var journal = new Journal(5); var reader = journal.Reader(pages: 1);
        var first = await reader.ReadAsync(default);
        Assert.IsTrue(first.ScanInProgress); Assert.IsNull(first.Observations);
        Assert.AreEqual(5L, first.UpperSequence); Assert.HasCount(1, journal.Calls);
        journal.Rows.Add(Observation(6));
        var second = await reader.ReadAsync(default);
        Assert.IsTrue(second.ScanInProgress); Assert.IsNull(second.Observations);
        var complete = await reader.ReadAsync(default);
        Assert.IsFalse(complete.ScanInProgress); Assert.IsFalse(complete.RequiresFullRescan);
        CollectionAssert.AreEqual(journal.Rows.Take(5).Select(row => row.OperationId).ToArray(),
            complete.Observations!.Select(row => row.OperationId).ToArray());
        Assert.AreEqual(1, journal.Begins); Assert.IsFalse(reader.HasPendingScan);
        var next = await reader.ReadAsync(default);
        Assert.AreEqual(6L, next.UpperSequence); Assert.AreEqual(2, journal.Begins);
        Assert.HasCount(5, complete.Observations!, "A later scan must not mutate the delivered original window.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    public async Task EmptyOrExactlyBudgetedTerminalPageCompletesInItsOriginalTurn(int count)
    {
        var journal = new Journal(count); var result = await journal.Reader(pages: 2).ReadAsync(default);
        Assert.IsFalse(result.ScanInProgress); Assert.IsFalse(result.RequiresFullRescan);
        Assert.HasCount(count, result.Observations!); Assert.IsLessThanOrEqualTo(2, journal.Calls.Count);
    }

    [TestMethod]
    public async Task CancellationDiscardsTheContinuationAndRetriesAllOriginalIdsFromZero()
    {
        var journal = new Journal(5); var reader = journal.Reader(pages: 1);
        Assert.IsTrue((await reader.ReadAsync(default)).ScanInProgress);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await reader.ReadAsync(cancel.Token));
        Assert.IsFalse(reader.HasPendingScan);
        MirrorPulseJournalWindowRead result;
        do { result = await reader.ReadAsync(default); } while (result.ScanInProgress);
        Assert.AreEqual(2, journal.Begins); Assert.AreEqual(0L, journal.Calls[1]);
        CollectionAssert.AreEqual(journal.Rows.Select(row => row.OperationId).ToArray(), result.Observations!.Select(row => row.OperationId).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LossAtCaptureOrAfterAPageWithholdsAllObservationsAndRequiresANewScan(bool afterPage)
    {
        var journal = new Journal(5) { CaptureLoss = !afterPage }; var reader = journal.Reader(pages: 1);
        if (afterPage) Assert.IsTrue((await reader.ReadAsync(default)).ScanInProgress);
        journal.PageLoss = afterPage;
        var loss = await reader.ReadAsync(default);
        Assert.IsTrue(loss.RequiresFullRescan); Assert.IsFalse(loss.ScanInProgress);
        Assert.IsNull(loss.Observations); Assert.IsFalse(reader.HasPendingScan);
        journal.CaptureLoss = false; journal.PageLoss = false;
        Assert.IsTrue((await reader.ReadAsync(default)).ScanInProgress);
        Assert.AreEqual(2, journal.Begins); Assert.AreEqual(0L, journal.Calls[^1]);
    }

    [TestMethod]
    public async Task ReadFailureAndExplicitReconciliationResetNeverRetainAPartialCursor()
    {
        var journal = new Journal(5); var reader = journal.Reader(pages: 1);
        await reader.ReadAsync(default); journal.FailRead = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () => await reader.ReadAsync(default));
        Assert.IsFalse(reader.HasPendingScan); journal.FailRead = false;
        await reader.ReadAsync(default); Assert.AreEqual(0L, journal.Calls[^1]);
        reader.Reset(); await reader.ReadAsync(default);
        Assert.AreEqual(3, journal.Begins); Assert.AreEqual(0L, journal.Calls[^1]);
    }

    [TestMethod]
    [DataRow("no-progress")]
    [DataRow("past-boundary")]
    [DataRow("unordered")]
    [DataRow("past-cursor")]
    [DataRow("oversized")]
    [DataRow("early-terminal")]
    public async Task InvalidPublicPageCannotExposeAnIncompleteWindow(string invalid)
    {
        var journal = new Journal(5);
        journal.Override = (_, _) => invalid switch
        {
            "no-progress" => new([], 0, true, false),
            "past-boundary" => new([], 6, true, false),
            "unordered" => new([Observation(2), Observation(1)], 2, true, false),
            "past-cursor" => new([Observation(2)], 1, true, false),
            "early-terminal" => new([Observation(1)], 1, false, false),
            _ => new([Observation(1), Observation(2), Observation(3)], 3, true, false)
        };
        var reader = journal.Reader(pages: 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reader.ReadAsync(default));
        Assert.IsFalse(reader.HasPendingScan);
    }

    [TestMethod]
    public async Task OriginalDependenciesAndMetadataOnOtherPagesStillPreventUnsafeCoalescing()
    {
        var instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.window"), instance,
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), [root]);
        Guid item = Guid.NewGuid(); var journal = new Journal(0);
        journal.Rows.AddRange([
            Observation(1) with { Kind = CloudLocalChangeKind.Delete, RelativePath = "Docs", IsDirectory = true },
            Observation(2) with { ItemId = item }, Observation(3) with { ItemId = item },
            Observation(4) with { ItemId = item, Kind = CloudLocalChangeKind.MetadataUpdate }
        ]);
        var reader = journal.Reader(pages: 1);
        Assert.IsNull((await reader.ReadAsync(default)).Observations);
        var complete = await reader.ReadAsync(default);
        var plan = MirrorPulseLocalBatchMapper.MapObservations(complete.Observations!, router);
        Assert.HasCount(4, plan.CompleteWindow!); Assert.HasCount(2, plan.Commands);
        Assert.IsTrue(MirrorPulseJournalOrderPolicy.DependsOn(plan.Commands[0], plan.CompleteWindow![0]));
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan(plan.CompleteWindow!, item, 4, new HashSet<Guid>()));
    }

    [TestMethod]
    public async Task ACompetingReaderCannotChangeTheCurrentOwnersContinuation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new Journal(1) { BeforeRead = async () => { entered.SetResult(); await released.Task; } };
        var reader = journal.Reader(pages: 1); var first = reader.ReadAsync(default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await reader.ReadAsync(default));
            Assert.ThrowsExactly<InvalidOperationException>(reader.Reset);
        }
        finally { released.SetResult(); }
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, result.Observations!); Assert.AreEqual(1, journal.Begins);
    }

    private static MirrorPulseLocalChangeObservation Observation(long sequence) => new(Guid.NewGuid(), sequence,
        CloudLocalChangeKind.ContentUpdate, Guid.NewGuid(), "Docs/original.bin", null, false, DateTimeOffset.UtcNow);

    private sealed record Scan(long Upper, bool Loss);

    private sealed class Journal
    {
        internal Journal(int count) { Rows = Enumerable.Range(1, count).Select(value => Observation(value)).ToList(); }
        internal List<MirrorPulseLocalChangeObservation> Rows { get; }
        internal List<long> Calls { get; } = [];
        internal int Begins { get; private set; }
        internal bool CaptureLoss { get; set; }
        internal bool PageLoss { get; set; }
        internal bool FailRead { get; set; }
        internal Func<Scan, long, MirrorPulseJournalWindowPage>? Override { get; set; }
        internal Func<Task>? BeforeRead { get; set; }

        internal MirrorPulseJournalWindowReader<Scan> Reader(int pages, int size = 2) => new(
            _ => { Begins++; return ValueTask.FromResult(new Scan(Rows.Count == 0 ? 0 : Rows.Max(row => row.Sequence), CaptureLoss)); },
            scan => scan.Upper, scan => scan.Loss, ReadAsync, pages, pageSize: size);

        private async ValueTask<MirrorPulseJournalWindowPage> ReadAsync(Scan scan, long after, int limit, CancellationToken token)
        {
            Calls.Add(after);
            if (BeforeRead is not null) await BeforeRead();
            token.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("Synthetic public journal read interruption.");
            if (PageLoss) return new([], after, true, true);
            if (Override is not null) return Override(scan, after);
            var rows = Rows.Where(row => row.Sequence > after && row.Sequence <= scan.Upper).Take(limit + 1).ToArray();
            bool more = rows.Length > limit;
            return new(rows.Take(limit).ToArray(), more ? rows[limit - 1].Sequence : scan.Upper, more, false);
        }
    }
}
