using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseConflictCommandDiagnosticsTests
{
    [TestMethod]
    public async Task SuccessfulCommandRunsOnceAndProducesNoFailureObservation()
    {
        int calls = 0, observations = 0;
        int value = await MirrorPulseConflictCommandDiagnostics.RunAsync(Guid.NewGuid(),
            () => MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.Acceptance,
                () => { calls++; return Task.FromResult(42); }),
            _ => { observations++; return ValueTask.CompletedTask; });
        Assert.AreEqual(42, value); Assert.AreEqual(1, calls); Assert.AreEqual(0, observations);
    }

    [TestMethod]
    public async Task RollingLogRetainsTheOriginalDeepestFailureWithoutPrivateTextOrData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-diagnostics", Guid.NewGuid().ToString("N"));
        var original = new InvalidDataException("synthetic-private-value-and-file-path");
        original.Data["arbitrary-field"] = "synthetic-private-value-and-file-path";
        Guid conflict = Guid.NewGuid();
        try
        {
            using var writer = new LocalRollingLogWriter(directory);
            var thrown = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseConflictCommandDiagnostics.RunAsync<int>(conflict,
                () => MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.Admission,
                    () => MirrorPulseConflictCommandDiagnostics.AtStageAsync<int>(MirrorPulseConflictCommandStage.LocalContent,
                        () => Task.FromException<int>(original))), async entry => await writer.WriteAsync(entry)));
            Assert.AreSame(original, thrown);
            Assert.AreEqual(MirrorPulseConflictCommandStage.LocalContent, MirrorPulseConflictCommandDiagnostics.Stage(thrown));
            var file = Directory.GetFiles(directory).Single();
            string text = await File.ReadAllTextAsync(file);
            Assert.IsFalse(text.Contains(original.Message, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("arbitrary-field", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(text);
            Assert.AreEqual("ConflictCommandFailed", json.RootElement.GetProperty("Code").GetString());
            var fields = json.RootElement.GetProperty("Fields");
            Assert.AreEqual(conflict.ToString("D"), fields.GetProperty("operationId").GetString());
            Assert.AreEqual("LocalContent", fields.GetProperty("conflictCommandStage").GetString());
            Assert.AreEqual("Unknown", fields.GetProperty("conflictCommandBoundary").GetString());
            Assert.AreEqual(6, fields.EnumerateObject().Count());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ALoggingFailureCannotReplaceTheOriginalCommandFailureOrInvokeItAgain()
    {
        var original = new InvalidOperationException("synthetic-private-message"); int calls = 0;
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseConflictCommandDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => { calls++; return Task.FromException<int>(original); }, _ => ValueTask.FromException(new IOException("synthetic-log-failure"))));
        Assert.AreSame(original, thrown); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("999")]
    [DataRow("LocalContent synthetic-private-value")]
    public void UnregisteredStageValuesCannotEnterTheSafeDiagnosticOutput(string value)
    {
        var entry = new LogEntry(LogLevel.Warning, "host", "ConflictCommandFailed", DateTimeOffset.UtcNow,
            [new("conflictCommandStage", value)]);
        var safe = SafeDiagnosticPolicy.Sanitize(entry);
        Assert.IsFalse(safe.Fields.ContainsKey("conflictCommandStage"));
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("KeepRemote requires the original registered file.", MirrorPulseConflictCommandBoundary.MissingMember)]
    [DataRow("KeepRemote requires the original file protection.", MirrorPulseConflictCommandBoundary.MissingProtection)]
    [DataRow("KeepRemote requires the original protected file and direct parent in its active role.", MirrorPulseConflictCommandBoundary.CurrentRole)]
    [DataRow("KeepRemote lost the original direct parent.", MirrorPulseConflictCommandBoundary.DirectParent)]
    [DataRow("KeepRemote requires the original protected file and direct parent before any write.", MirrorPulseConflictCommandBoundary.NativeObject)]
    [DataRow("KeepRemote requires the exact original resident placeholder without source recall.", MirrorPulseConflictCommandBoundary.ResidentIdentity)]
    [DataRow("A file start requires the complete original UpdateFile decision, copies, journal, object, identity and direct parent.", MirrorPulseConflictCommandBoundary.StartRelations)]
    [DataRow("synthetic-private KeepRemote requires the original registered file.", MirrorPulseConflictCommandBoundary.Unknown)]
    [DataRow("KeepRemote requires the original registered file. synthetic-private", MirrorPulseConflictCommandBoundary.Unknown)]
    public async Task OnlyAnExactProductGuardProducesAClosedBoundaryWithoutRecordingItsText(string message,
        MirrorPulseConflictCommandBoundary expected)
    {
        var original = new InvalidDataException(message); LogEntry observed = null!;
        var thrown = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseConflictCommandDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => Task.FromException<int>(original), entry => { observed = entry; return ValueTask.CompletedTask; }));
        Assert.AreSame(original, thrown);
        var safe = SafeDiagnosticPolicy.Sanitize(observed);
        Assert.AreEqual(expected.ToString(), safe.Fields["conflictCommandBoundary"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains(message, StringComparison.Ordinal));
    }
}
