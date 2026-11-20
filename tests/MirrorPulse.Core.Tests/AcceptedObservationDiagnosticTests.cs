using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AcceptedObservationDiagnosticTests
{
    [TestMethod]
    public void AtomicDirectoryDiagnosticsExposeOnlyKnownWorkerCodesAndPhases()
    {
        var rejected = new AdapterWorkerOperationException("AtomicDirectoryJournalRecordInvalid", directoryAdmission: false);
        var unknown = new AdapterWorkerOperationException("unrecognized worker response", directoryAdmission: true);
        Assert.AreEqual("AtomicDirectoryJournalRecordInvalid", rejected.FailureCode);
        Assert.AreEqual("Delete", rejected.DirectoryPhase);
        Assert.AreEqual("Unknown", unknown.FailureCode);
        Assert.AreEqual("Admission", unknown.DirectoryPhase);
        Assert.AreEqual("Unknown", new AdapterWorkerOperationException(rejected.FailureCode).FailureCode);
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "CloudFiles.Upload",
            "AtomicDirectoryJournalAcknowledgementFailed", DateTimeOffset.UtcNow,
            [new("workerFailureCode", rejected.FailureCode), new("workerDirectoryPhase", rejected.DirectoryPhase!)]));
        Assert.AreEqual("AtomicDirectoryJournalAcknowledgementFailed", safe.Code);
        Assert.AreEqual(rejected.FailureCode, safe.Fields["workerFailureCode"]);
        Assert.AreEqual(rejected.DirectoryPhase, safe.Fields["workerDirectoryPhase"]);
    }

    [TestMethod]
    [DataRow("JournalAcceptedContentObservation")]
    [DataRow("JournalAcceptedContentAcknowledged")]
    public void OriginalCorrelationAndDigestsSurviveSanitizationWithoutRawSourceValues(string code)
    {
        Guid original = Guid.NewGuid(), correlation = Guid.NewGuid();
        string digest = new('a', 64);
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "CloudFiles.Upload", code, DateTimeOffset.UtcNow,
            [new("operationId", original.ToString("D")), new("correlationId", correlation.ToString("D")),
             new("journalSequence", "42"), new("journalPayloadSha256", digest), new("nativeIdentitySha256", digest),
             new("nativeBindingSha256", digest), new("acknowledgedRevisionSha256", digest),
             new("remoteRevision", "PRIVATE_REVISION"), new("relativePath", "PRIVATE_PATH"), new("token", "PRIVATE_TOKEN")]));
        Assert.AreEqual(code, safe.Code);
        Assert.AreEqual(original.ToString("D"), safe.Fields["operationId"]);
        Assert.AreEqual(correlation.ToString("D"), safe.Fields["correlationId"]);
        Assert.AreEqual("42", safe.Fields["journalSequence"]);
        foreach (string name in new[] { "journalPayloadSha256", "nativeIdentitySha256", "nativeBindingSha256", "acknowledgedRevisionSha256" })
            Assert.AreEqual(digest.ToUpperInvariant(), safe.Fields[name]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("PRIVATE_VALUE")]
    [DataRow("0123456789ABCDEF")]
    [DataRow("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ArbitrarySourceValuesCannotEnterDigestFields(string value)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "CloudFiles.Upload", "JournalAcceptedContentObservation", DateTimeOffset.UtcNow,
            [new("journalPayloadSha256", value), new("nativeIdentitySha256", value), new("nativeBindingSha256", value), new("acknowledgedRevisionSha256", value)]));
        foreach (string name in new[] { "journalPayloadSha256", "nativeIdentitySha256", "nativeBindingSha256", "acknowledgedRevisionSha256" })
            Assert.IsFalse(safe.Fields.ContainsKey(name));
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("9223372036854775808")]
    [DataRow("PRIVATE_SEQUENCE")]
    public void MalformedOriginalSequenceIsOmitted(string sequence)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "CloudFiles.Upload", "JournalAcceptedContentObservation", DateTimeOffset.UtcNow,
            [new("journalSequence", sequence)]));
        Assert.IsFalse(safe.Fields.ContainsKey("journalSequence"));
    }
}
