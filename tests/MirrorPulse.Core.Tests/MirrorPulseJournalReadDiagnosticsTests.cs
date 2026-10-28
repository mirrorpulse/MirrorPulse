using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalReadDiagnosticsTests
{
    [TestMethod]
    public async Task DeepestReadBoundaryRetainsTheSameArgumentFailureAndOnlyFixedLogFields()
    {
        var original = new ArgumentException("PRIVATE_PATH token=PRIVATE_SECRET");
        var caught = await Assert.ThrowsExactlyAsync<ArgumentException>(() => MirrorPulseJournalReadDiagnostics.AtStageAsync(
            MirrorPulseJournalReadStage.JournalSource, () => MirrorPulseJournalReadDiagnostics.AtStageAsync<int>(
                MirrorPulseJournalReadStage.RootNativeAudit, () => ValueTask.FromException<int>(original))).AsTask());
        Assert.AreSame(original, caught);
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "CloudFiles.Upload", "JournalReadFailed", DateTimeOffset.UtcNow,
            [new("journalReadStage", MirrorPulseJournalReadDiagnostics.Stage(caught).ToString()),
             new("journalReadFailureKind", MirrorPulseJournalReadDiagnostics.Kind(caught)), new("hresult", caught.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]));
        Assert.AreEqual("RootNativeAudit", safe.Fields["journalReadStage"]);
        Assert.AreEqual("Argument", safe.Fields["journalReadFailureKind"]); Assert.AreEqual("80070057", safe.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationRetainsTheOriginalTokenAndReadBoundary()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var original = new OperationCanceledException(stop.Token);
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseJournalReadDiagnostics.AtStageAsync(
            MirrorPulseJournalReadStage.FeedWindow, () => ValueTask.FromException(original)).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken);
        Assert.AreEqual(MirrorPulseJournalReadStage.FeedWindow, MirrorPulseJournalReadDiagnostics.Stage(caught));
    }

    [TestMethod]
    public async Task UnavailableDiagnosticDataCannotReplaceTheOriginalFailure()
    {
        var original = new UnavailableDataException();
        var caught = await Assert.ThrowsExactlyAsync<UnavailableDataException>(() => MirrorPulseJournalReadDiagnostics.AtStageAsync<int>(
            MirrorPulseJournalReadStage.RootAcknowledgement, () => ValueTask.FromException<int>(original)).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(MirrorPulseJournalReadStage.Unknown, MirrorPulseJournalReadDiagnostics.Stage(caught));
    }

    [TestMethod]
    public async Task SuccessfulGenericAndVoidActionsRunOnceAndReturnTheOriginalResult()
    {
        int calls = 0; var original = new object();
        Assert.AreSame(original, await MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.JournalAdmission,
            () => { calls++; return ValueTask.FromResult(original); }));
        await MirrorPulseJournalReadDiagnostics.AtStageAsync(MirrorPulseJournalReadStage.RootReadmissionAudit,
            () => { calls++; return ValueTask.CompletedTask; });
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow("PRIVATE_SECRET")]
    [DataRow("1")]
    [DataRow("-1")]
    public void UnknownOrNumericStageAndKindTextCannotEnterOrdinaryLogs(string value)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "CloudFiles.Upload", "JournalReadFailed", DateTimeOffset.UtcNow,
            [new("journalReadStage", value), new("journalReadFailureKind", value)]));
        Assert.IsFalse(safe.Fields.ContainsKey("journalReadStage")); Assert.IsFalse(safe.Fields.ContainsKey("journalReadFailureKind"));
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnmarkedFailuresStayUnknownAndNativeKindsDoNotBecomeLibraryDefectClaims()
    {
        var native = new Win32Exception(87, "PRIVATE_NATIVE");
        Assert.AreEqual("Native", MirrorPulseJournalReadDiagnostics.Kind(native));
        Assert.AreEqual(MirrorPulseJournalReadStage.Unknown, MirrorPulseJournalReadDiagnostics.Stage(native));
        Assert.AreEqual("Argument", MirrorPulseJournalReadDiagnostics.Kind(new ArgumentException("PRIVATE_ARGUMENT")));
        Assert.AreEqual("Other", MirrorPulseJournalReadDiagnostics.Kind(new NotSupportedException("PRIVATE_OTHER")));
    }

    private sealed class UnavailableDataException : Exception
    {
        public override IDictionary Data => throw new IOException("PRIVATE_DIAGNOSTIC");
    }
}
