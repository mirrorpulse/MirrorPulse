using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationFailureDiagnosticsTests
{
    [TestMethod]
    public async Task EveryClosedCancellationStageRetainsValidationFailureWithoutRecordingItsMessage()
    {
        foreach (var stage in Enum.GetValues<MirrorPulseRootLabelCancellationStage>())
        {
            var original = new InvalidDataException("PRIVATE_PATH PRIVATE_TOKEN");
            SafeLogEntry? observed = null;
            var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(Guid.NewGuid(),
                () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(stage, () => ValueTask.FromException<int>(original)),
                entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
            Assert.AreSame(original, caught); Assert.IsNotNull(observed);
            Assert.AreEqual(stage.ToString(), observed.Fields["rootLabelCancellationStage"]);
            Assert.IsFalse(JsonSerializer.Serialize(observed).Contains("PRIVATE_", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task CancellationFailureRetainsTheOriginalExceptionAndOnlyFixedFields()
    {
        Guid operation = Guid.NewGuid(); var original = new InvalidOperationException("PRIVATE_PATH token=PRIVATE_SECRET");
        SafeLogEntry? observed = null;
        var caught = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(operation,
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelCancellationStage.OriginalCancellation,
                () => ValueTask.FromException<int>(original)), entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.IsNotNull(observed);
        Assert.AreEqual("RootLabelCancellationFailed", observed.Code); Assert.AreEqual(operation.ToString("D"), observed.Fields["operationId"]);
        Assert.AreEqual("OriginalCancellation", observed.Fields["rootLabelCancellationStage"]);
        Assert.AreEqual("Unknown", observed.Fields["rootLabelFailureBoundary"]); Assert.AreEqual("80131509", observed.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(observed).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NestedCancellationStagesKeepTheDeepestFailureAndIndependentPreparationStage()
    {
        var original = new InvalidDataException("PRIVATE_MEMBER");
        MirrorPulseRootLabelFailureDiagnostics.MarkPreparationStage(original, MirrorPulseRootLabelPreparationStage.PublicProof);
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(
            MirrorPulseRootLabelCancellationStage.InstanceLibrary, () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(
                MirrorPulseRootLabelCancellationStage.MetadataInventory, () => ValueTask.FromException<int>(original))).AsTask());
        Assert.AreSame(original, caught);
        Assert.AreEqual(MirrorPulseRootLabelCancellationStage.MetadataInventory, MirrorPulseRootLabelCancellationFailureDiagnostics.Stage(caught));
        Assert.AreEqual(MirrorPulseRootLabelPreparationStage.PublicProof, MirrorPulseRootLabelFailureDiagnostics.PreparationStage(caught));
    }

    [TestMethod]
    public async Task ObservationFailureCannotReplaceTheOriginalProductFailure()
    {
        var original = new InvalidDataException("PRIVATE_PRODUCT");
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(
            Guid.NewGuid(), () => ValueTask.FromException<int>(original), _ => ValueTask.FromException(new IOException("PRIVATE_OBSERVER"))).AsTask());
        Assert.AreSame(original, caught);
    }

    [TestMethod]
    public async Task CancellationCancellationRetainsTheOriginalTokenExceptionAndStage()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var original = new OperationCanceledException(stop.Token);
        SafeLogEntry? observed = null;
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelCancellationStage.RootWorkDrain,
                () => ValueTask.FromException<int>(original)), entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken); Assert.IsNotNull(observed);
        Assert.AreEqual("RootWorkDrain", observed.Fields["rootLabelCancellationStage"]);
    }

    [TestMethod]
    public async Task SuccessfulCancellationReturnsTheSameResultWithoutObservation()
    {
        var original = new object(); int calls = 0;
        var result = await MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(Guid.NewGuid(),
            () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelCancellationStage.OriginalCancellation,
                () => ValueTask.FromResult(original)), _ => { calls++; return ValueTask.CompletedTask; });
        Assert.AreSame(original, result); Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task InvalidCancellationStagesAndArgumentsCannotInvokeTheOperation()
    {
        bool called = false;
        ValueTask<int> Action() { called = true; return ValueTask.FromResult(1); }
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(Guid.Empty, Action,
            _ => ValueTask.CompletedTask).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync(
            (MirrorPulseRootLabelCancellationStage)int.MaxValue, Action).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(
            MirrorPulseRootLabelCancellationStage.OriginalCancellation, null!).AsTask());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.MarkStage(new InvalidOperationException(),
            (MirrorPulseRootLabelCancellationStage)int.MaxValue));
        Assert.IsFalse(called);
    }

    [TestMethod]
    public void CancellationStageFieldsRejectArbitraryNumericAndWrongCaseValues()
    {
        foreach (string value in new[] { "PRIVATE_SECRET", "1", "originalCancellation", "OriginalCancellation PRIVATE_TOKEN" })
        {
            var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "host", "RootLabelCancellationFailed", DateTimeOffset.UtcNow,
                [new("rootLabelCancellationStage", value)]));
            Assert.IsFalse(safe.Fields.ContainsKey("rootLabelCancellationStage"));
        }
        Assert.AreEqual(MirrorPulseRootLabelCancellationStage.Unknown, MirrorPulseRootLabelCancellationFailureDiagnostics.Stage(new IOException()));
    }

    [TestMethod]
    public async Task RollingCancellationLogKeepsFixedStageAndOriginalOperationWithoutPrivateExceptionData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-cancellation-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Guid operation = Guid.NewGuid(); var original = new InvalidOperationException("PRIVATE_PATH password=PRIVATE_SECRET");
            using (var writer = new LocalRollingLogWriter(directory))
                _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync(operation,
                    () => MirrorPulseRootLabelCancellationFailureDiagnostics.AtStageAsync<int>(MirrorPulseRootLabelCancellationStage.CurrentTreeAudit,
                        () => ValueTask.FromException<int>(original)), async entry => await writer.WriteAsync(entry)).AsTask());
            string text = await File.ReadAllTextAsync(Path.Combine(directory, "mirrorpulse.log"));
            using var json = JsonDocument.Parse(text); var fields = json.RootElement.GetProperty("Fields");
            Assert.AreEqual("CurrentTreeAudit", fields.GetProperty("rootLabelCancellationStage").GetString());
            Assert.AreEqual(operation.ToString("D"), fields.GetProperty("operationId").GetString());
            Assert.IsFalse(text.Contains("PRIVATE_", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }
}
