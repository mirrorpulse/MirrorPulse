using System.Collections.Concurrent;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelExecutionProgressDiagnosticsTests
{
    private static readonly string[] FourSequenceValues = ["1", "2", "3", "4"];

    [TestMethod]
    public async Task PendingExecutionPersistsItsBoundaryBeforeTheOriginalWorkCompletes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-execution-progress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid operation = Guid.NewGuid();
        Task<int> work = MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync(operation,
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.InstanceLibrary,
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
            Assert.AreEqual("RootLabelExecutionProgress", last.Code);
            Assert.AreEqual(operation.ToString("D"), last.Fields["operationId"]);
            Assert.AreEqual("InstanceLibrary", last.Fields["rootLabelExecutionStage"]);
            Assert.AreEqual("Begin", last.Fields["executionProgressPhase"]);
            Assert.HasCount(5, last.Fields);
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
    public async Task ProgressPreservesTheOriginalCancellationExceptionAndDeepestStage()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var original = new OperationCanceledException("PRIVATE_PATH PRIVATE_TOKEN", stop.Token);
        var records = new List<SafeLogEntry>();
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.OriginalExecution,
                () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelExecutionStage.SubtreeMemberInspection,
                    () => ValueTask.FromException<int>(original))), entry => { records.Add(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken);
        Assert.AreEqual(MirrorPulseRootLabelExecutionStage.SubtreeMemberInspection, MirrorPulseRootLabelExecutionFailureDiagnostics.Stage(caught));
        Assert.AreEqual("SubtreeMemberInspection", records.Single(record => record.Code == "RootLabelExecutionFailed").Fields["rootLabelExecutionStage"]);
        Assert.AreEqual("Failed", records[^1].Fields["executionProgressPhase"]);
        Assert.IsFalse(JsonSerializer.Serialize(records).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BrokenObservationCannotReplaceSuccessOrTheOriginalFailure()
    {
        int calls = 0;
        ValueTask Observe(LogEntry _) { calls++; return ValueTask.FromException(new IOException("PRIVATE_OBSERVER")); }
        Assert.AreEqual(19, await MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.CurrentTreeAudit, () => ValueTask.FromResult(19)), Observe));
        Assert.AreEqual(1, calls);
        var original = new InvalidDataException("PRIVATE_PRODUCT"); calls = 0;
        Assert.AreSame(original, await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync<int>(
            Guid.NewGuid(), () => ValueTask.FromException<int>(original), Observe).AsTask()));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ConcurrentExecutionScopesHaveIndependentBoundedSequences()
    {
        var records = new ConcurrentQueue<SafeLogEntry>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid[] operations = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        Task<int>[] work = operations.Select(operation => MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync(operation,
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.RootWorkDrain,
                async () => { await release.Task; return 23; }), entry => { records.Enqueue(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; }).AsTask()).ToArray();
        Assert.HasCount(16, records); release.SetResult();
        Assert.IsTrue((await Task.WhenAll(work)).All(result => result == 23));
        foreach (Guid operation in operations)
        {
            var own = records.Where(record => record.Fields["operationId"] == operation.ToString("D")).ToArray();
            CollectionAssert.AreEqual(FourSequenceValues, own.Select(record => record.Fields["executionSequence"]).ToArray());
        }
        records.Clear();
        await MirrorPulseRootLabelExecutionFailureDiagnostics.RunWithProgressAsync(Guid.NewGuid(), async () =>
        {
            for (int i = 0; i < 40; i++)
                _ = await MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.CurrentTreeAudit, () => ValueTask.FromResult(1));
            return 29;
        }, entry => { records.Enqueue(SafeDiagnosticPolicy.Sanitize(entry)); return ValueTask.CompletedTask; });
        Assert.HasCount(32, records);
    }

    [TestMethod]
    [DataRow("executionProgressPhase", "PRIVATE_TOKEN")]
    [DataRow("executionSequence", "-1")]
    [DataRow("executionElapsedMs", "PRIVATE_PATH")]
    public void ExecutionProgressFieldsRejectUnstructuredValues(string key, string value)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "host", "RootLabelExecutionProgress", DateTimeOffset.UtcNow, [new(key, value)]));
        Assert.IsFalse(safe.Fields.ContainsKey(key));
    }
}
