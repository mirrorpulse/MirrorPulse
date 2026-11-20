using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelExecutionFailureDiagnosticsTests
{
    [TestMethod]
    public async Task ExecutionFailureRetainsTheOriginalExceptionAndOnlyFixedFields()
    {
        Guid operation = Guid.NewGuid(); var original = new InvalidOperationException("PRIVATE_PATH token=PRIVATE_SECRET");
        SafeLogEntry? observed = null;
        var caught = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(operation,
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelExecutionStage.OriginalExecution,
                () => ValueTask.FromException<int>(original)), entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.IsNotNull(observed);
        Assert.AreEqual("RootLabelExecutionFailed", observed.Code); Assert.AreEqual(operation.ToString("D"), observed.Fields["operationId"]);
        Assert.AreEqual("OriginalExecution", observed.Fields["rootLabelExecutionStage"]);
        Assert.AreEqual("Unknown", observed.Fields["rootLabelFailureBoundary"]); Assert.AreEqual("80131509", observed.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(observed).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NestedExecutionStagesKeepTheDeepestFailureAndIndependentPreparationStage()
    {
        var original = new InvalidDataException("PRIVATE_MEMBER");
        MirrorPulseRootLabelFailureDiagnostics.MarkPreparationStage(original, MirrorPulseRootLabelPreparationStage.PublicProof);
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(
            MirrorPulseRootLabelExecutionStage.InstanceLibrary, () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(
                MirrorPulseRootLabelExecutionStage.SubtreePreparation, () => ValueTask.FromException<int>(original))).AsTask());
        Assert.AreSame(original, caught);
        Assert.AreEqual(MirrorPulseRootLabelExecutionStage.SubtreePreparation, MirrorPulseRootLabelExecutionFailureDiagnostics.Stage(caught));
        Assert.AreEqual(MirrorPulseRootLabelPreparationStage.PublicProof, MirrorPulseRootLabelFailureDiagnostics.PreparationStage(caught));
    }

    [TestMethod]
    public async Task ObservationFailureCannotReplaceTheOriginalProductFailure()
    {
        var original = new InvalidDataException("PRIVATE_PRODUCT");
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync<int>(
            Guid.NewGuid(), () => ValueTask.FromException<int>(original), _ => ValueTask.FromException(new IOException("PRIVATE_OBSERVER"))).AsTask());
        Assert.AreSame(original, caught);
    }

    [TestMethod]
    public async Task ExecutionCancellationRetainsTheOriginalTokenExceptionAndStage()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var original = new OperationCanceledException(stop.Token);
        SafeLogEntry? observed = null;
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelExecutionStage.RootWorkDrain,
                () => ValueTask.FromException<int>(original)), entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken); Assert.IsNotNull(observed);
        Assert.AreEqual("RootWorkDrain", observed.Fields["rootLabelExecutionStage"]);
    }

    [TestMethod]
    public async Task SuccessfulExecutionReturnsTheSameResultWithoutObservation()
    {
        var original = new object(); int calls = 0;
        var result = await MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.OriginalExecution,
                () => ValueTask.FromResult(original)), _ => { calls++; return ValueTask.CompletedTask; });
        Assert.AreSame(original, result); Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task InvalidExecutionStagesAndArgumentsCannotInvokeTheOperation()
    {
        bool called = false;
        ValueTask<int> Action() { called = true; return ValueTask.FromResult(1); }
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(Guid.Empty, Action,
            _ => ValueTask.CompletedTask).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(
            (MirrorPulseRootLabelExecutionStage)int.MaxValue, Action).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(
            MirrorPulseRootLabelExecutionStage.OriginalExecution, null!).AsTask());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.MarkStage(new InvalidOperationException(),
            (MirrorPulseRootLabelExecutionStage)int.MaxValue));
        Assert.IsFalse(called);
    }

    [TestMethod]
    public void ExecutionStageFieldsRejectArbitraryNumericAndWrongCaseValues()
    {
        foreach (string value in new[] { "PRIVATE_SECRET", "1", "originalExecution", "OriginalExecution PRIVATE_TOKEN" })
        {
            var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "host", "RootLabelExecutionFailed", DateTimeOffset.UtcNow,
                [new("rootLabelExecutionStage", value)]));
            Assert.IsFalse(safe.Fields.ContainsKey("rootLabelExecutionStage"));
        }
        Assert.AreEqual(MirrorPulseRootLabelExecutionStage.Unknown, MirrorPulseRootLabelExecutionFailureDiagnostics.Stage(new IOException()));
    }

    [TestMethod]
    public async Task RollingExecutionLogKeepsFixedStageAndOriginalOperationWithoutPrivateExceptionData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-execution-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Guid operation = Guid.NewGuid(); var original = new InvalidOperationException("PRIVATE_PATH password=PRIVATE_SECRET");
            using (var writer = new LocalRollingLogWriter(directory))
                _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(operation,
                    () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelExecutionStage.CurrentTreeAudit,
                        () => ValueTask.FromException<int>(original)), async entry => await writer.WriteAsync(entry)).AsTask());
            string text = await File.ReadAllTextAsync(Path.Combine(directory, "mirrorpulse.log"));
            using var json = JsonDocument.Parse(text); var fields = json.RootElement.GetProperty("Fields");
            Assert.AreEqual("CurrentTreeAudit", fields.GetProperty("rootLabelExecutionStage").GetString());
            Assert.AreEqual(operation.ToString("D"), fields.GetProperty("operationId").GetString());
            Assert.IsFalse(text.Contains("PRIVATE_", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }
}
