using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationProgressDiagnosticsTests
{
    private static readonly string[] RequestStageNames = ["Unknown", "RootWorkDrain", "InstanceLibrary", "PendingPermissionRecovery", "OriginalCancellation", "CurrentTreeRecovery", "CurrentTreeAudit",
        "PendingCancellationInitialization", "PendingOriginalPermissions", "PendingBornPermissions", "CurrentTreeAnchorRecovery", "CurrentTreeMemberRecovery", "CurrentTreeInitializationAudit"];
    private static readonly string[] FourSequenceValues = ["1", "2", "3", "4"];

    [TestMethod]
    public async Task PendingCancellationWritesItsStartedBoundaryBeforeTheActionCompletes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-cancellation-progress-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid operation = Guid.NewGuid();
        Task<int> work = MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation,
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.RootWorkDrain,
                () => { entered.SetResult(); return new ValueTask<int>(release.Task); }), async entry =>
            {
                using var writer = new LocalRollingLogWriter(directory);
                await writer.WriteAsync(entry);
            }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(work.IsCompleted);
            string[] lines = await File.ReadAllLinesAsync(Path.Combine(directory, "mirrorpulse.log"));
            Assert.HasCount(2, lines);
            var last = JsonSerializer.Deserialize<SafeLogEntry>(lines[^1])!;
            Assert.AreEqual("RootLabelCancellationProgress", last.Code);
            Assert.AreEqual(operation.ToString("D"), last.Fields["operationId"]);
            Assert.AreEqual("RootWorkDrain", last.Fields["rootLabelCancellationStage"]);
            Assert.AreEqual("Begin", last.Fields["cancellationProgressPhase"]);
            release.SetResult(17);
            Assert.AreEqual(17, await work);
            Assert.HasCount(4, await File.ReadAllLinesAsync(Path.Combine(directory, "mirrorpulse.log")));
        }
        finally
        {
            release.TrySetResult(17);
            await work;
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task OnlyRequestAndRecoveryBoundariesProduceFixedTypedProgress()
    {
        var records = new List<SafeLogEntry>();
        Guid operation = Guid.NewGuid();
        int result = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation, async () =>
        {
            foreach (var stage in Enum.GetValues<MirrorPulseRootLabelCancellationStage>())
                Assert.AreEqual(7, await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(stage, () => ValueTask.FromResult(7)));
            return 19;
        }, entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        Assert.AreEqual(19, result);
        Assert.HasCount(26, records);
        CollectionAssert.AreEqual(RequestStageNames,
            records.Select(record => record.Fields["rootLabelCancellationStage"]).Distinct().ToArray());
        long elapsed = 0;
        for (int i = 0; i < records.Count; i++)
        {
            Assert.AreEqual(operation.ToString("D"), records[i].Fields["operationId"]);
            Assert.AreEqual((i + 1).ToString(CultureInfo.InvariantCulture), records[i].Fields["cancellationSequence"]);
            Assert.IsGreaterThanOrEqualTo(elapsed, long.Parse(records[i].Fields["cancellationElapsedMs"], CultureInfo.InvariantCulture));
            elapsed = long.Parse(records[i].Fields["cancellationElapsedMs"], CultureInfo.InvariantCulture);
            Assert.HasCount(5, records[i].Fields);
        }
    }

    [TestMethod]
    public async Task ProgressRetainsOriginalCancellationTokenExceptionAndDeepestStage()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var original = new OperationCanceledException("PRIVATE_PATH PRIVATE_TOKEN", stop.Token);
        var records = new List<SafeLogEntry>();
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.PendingPermissionRecovery,
                () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelCancellationStage.MemberNativeObject,
                    () => ValueTask.FromException<int>(original))), entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken);
        Assert.AreEqual(MirrorPulseRootLabelCancellationStage.MemberNativeObject, MirrorPulseRootLabelCancellationFailureDiagnostics.Stage(caught));
        var failure = records.Single(record => record.Code == "RootLabelCancellationFailed");
        Assert.AreEqual("MemberNativeObject", failure.Fields["rootLabelCancellationStage"]);
        Assert.AreEqual("Failed", records[^1].Fields["cancellationProgressPhase"]);
        Assert.IsFalse(JsonSerializer.Serialize(records).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ObservationFailuresDoNotReplaceSuccessfulOrFailedWork()
    {
        int observed = 0;
        ValueTask Observe(LogEntry _) { observed++; return ValueTask.FromException(new IOException("PRIVATE_OBSERVER")); }
        var expected = new object();
        Assert.AreSame(expected, await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.OriginalCancellation, () => ValueTask.FromResult(expected)), Observe));
        Assert.AreEqual(1, observed);
        var original = new InvalidDataException("PRIVATE_PRODUCT"); observed = 0;
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync<int>(Guid.NewGuid(),
            () => ValueTask.FromException<int>(original), Observe).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(2, observed);
    }

    [TestMethod]
    public async Task ConcurrentRequestsRetainIndependentOperationsAndSequences()
    {
        var records = new ConcurrentQueue<SafeLogEntry>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid[] operations = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        Task<int>[] work = operations.Select(operation => MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(operation,
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.InstanceLibrary, async () =>
            { await release.Task; return 23; }), entry => { records.Enqueue(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }).AsTask()).ToArray();
        Assert.HasCount(16, records); release.SetResult();
        Assert.IsTrue((await Task.WhenAll(work)).All(result => result == 23));
        foreach (Guid operation in operations)
        {
            var own = records.Where(record => record.Fields["operationId"] == operation.ToString("D")).ToArray();
            Assert.HasCount(4, own);
            CollectionAssert.AreEqual(FourSequenceValues, own.Select(record => record.Fields["cancellationSequence"]).ToArray());
        }
    }

    [TestMethod]
    public async Task NestedRequestScopesRestoreTheirPreviousOperation()
    {
        var records = new List<SafeLogEntry>(); Guid outer = Guid.NewGuid(), inner = Guid.NewGuid();
        ValueTask Observe(LogEntry entry) { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(outer, async () =>
        {
            _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(inner,
                () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.RootWorkDrain, () => ValueTask.FromResult(1)), Observe);
            return await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeAudit, () => ValueTask.FromResult(2));
        }, Observe);
        Assert.HasCount(4, records.Where(record => record.Fields["operationId"] == inner.ToString("D")).ToArray());
        Assert.HasCount(4, records.Where(record => record.Fields["operationId"] == outer.ToString("D")).ToArray());
        int before = records.Count;
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.RootWorkDrain, () => ValueTask.FromResult(3));
        _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(Guid.NewGuid(), () => ValueTask.FromResult(4), Observe);
        Assert.HasCount(before, records);
    }

    [TestMethod]
    public async Task RepeatedStagesHaveABoundedObservationBudget()
    {
        var records = new List<SafeLogEntry>(); int actions = 0;
        int result = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(), async () =>
        {
            for (int i = 0; i < 100; i++)
                _ = await MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.CurrentTreeAudit, () => ValueTask.FromResult(++actions));
            return actions;
        }, entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        Assert.AreEqual(100, result); Assert.HasCount(32, records);
        CollectionAssert.AreEqual(Enumerable.Range(1, 32).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(), records.Select(record => record.Fields["cancellationSequence"]).ToArray());
    }

    [TestMethod]
    public void SanitizationRejectsUntrustedProgressFields()
    {
        foreach (string value in new[] { "PRIVATE_SECRET", "-1", "1", "begin", "Completed PRIVATE_TOKEN" })
        {
            var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "host", "RootLabelCancellationProgress", DateTimeOffset.UtcNow,
                [new("cancellationProgressPhase", value), new("cancellationElapsedMs", "PRIVATE_PATH"), new("cancellationSequence", "-1")]));
            Assert.HasCount(2, safe.Fields);
            Assert.AreEqual("3", safe.Fields["omittedFieldCount"]);
            Assert.AreEqual(LogFieldPolicy.RedactedValue, safe.Fields["redacted"]);
            Assert.IsFalse(safe.Fields.ContainsKey("cancellationProgressPhase"));
            Assert.IsFalse(safe.Fields.ContainsKey("cancellationElapsedMs"));
            Assert.IsFalse(safe.Fields.ContainsKey("cancellationSequence"));
            Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
        }
    }
}
