using System.IO.Compression;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SafeDiagnosticPolicyTests
{
    [TestMethod]
    public async Task ArbitrarySecretsInEveryInputSurfaceAreAbsentFromLogsAndExportedZip()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-safe-diagnostics", Guid.NewGuid().ToString("N"));
        string hostile = "password-needle token-needle Authorization: Bearer auth-needle Cookie=cookie-needle " +
            "https://user-needle:pass-needle@example.invalid/private?token=query-needle exception-secret-needle";
        Guid operationId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        try
        {
            using var writer = new LocalRollingLogWriter(fixture);
            await writer.WriteAsync(new LogEntry(LogLevel.Error, hostile, hostile, DateTimeOffset.UtcNow,
                [new("description", hostile), new(hostile, hostile), new("Authorization", hostile), new("Cookie", hostile),
                 new("hresult", "80070005"), new("operationId", operationId.ToString("D")), new("kind", hostile)]));
            string log = await File.ReadAllTextAsync(writer.FilePath);
            Assert.IsFalse(log.Contains("needle", StringComparison.Ordinal));
            using JsonDocument stored = JsonDocument.Parse(log);
            Guid diagnosticId = stored.RootElement.GetProperty("DiagnosticId").GetGuid();
            string legacy = Path.Combine(fixture, "token-needle.log");
            await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new
            {
                Message = hostile,
                Category = hostile,
                Detail = hostile,
                Url = hostile,
                Fields = new Dictionary<string, string> { ["operationId"] = hostile, [hostile] = hostile }
            }) + "\n" + hostile);
            var diagnostic = new DiagnosticEvent(eventId, hostile, new Diagnostic(hostile, hostile, DiagnosticSeverity.Error, hostile),
                DateTimeOffset.UtcNow, hostile, new Dictionary<string, string> { ["description"] = hostile, [hostile] = hostile, ["operationId"] = operationId.ToString("D") });
            string exported = await DiagnosticPackageExporter.ExportAsync(Path.Combine(fixture, "safe.zip"), [diagnostic], [writer.FilePath, legacy]);
            using ZipArchive archive = ZipFile.OpenRead(exported);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                Assert.IsFalse(entry.FullName.Contains("needle", StringComparison.Ordinal));
                using var reader = new StreamReader(entry.Open());
                Assert.IsFalse((await reader.ReadToEndAsync()).Contains("needle", StringComparison.Ordinal));
            }
            using var exportedLogReader = new StreamReader(archive.GetEntry("logs/mirrorpulse.log")!.Open());
            using JsonDocument exportedLog = JsonDocument.Parse(await exportedLogReader.ReadToEndAsync());
            Assert.AreEqual(diagnosticId, exportedLog.RootElement.GetProperty("DiagnosticId").GetGuid());
            Assert.AreEqual(operationId.ToString("D"), exportedLog.RootElement.GetProperty("Fields").GetProperty("operationId").GetString());
            using var eventReader = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open());
            using JsonDocument exportedEvent = JsonDocument.Parse(await eventReader.ReadToEndAsync());
            Assert.AreEqual(eventId, exportedEvent.RootElement.GetProperty("EventId").GetGuid());
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public void RegisteredFailurePreservesNativeCodeAndCorrelationWithoutExceptionText()
    {
        Guid operation = Guid.NewGuid();
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Error, "CloudFiles.Upload", "JournalAcknowledgementFailed", DateTimeOffset.UtcNow,
            [new("operationId", operation.ToString("D")), new("hresult", "80070005"), new("failureCategory", "IO"),
             new("reason", "token-secret-needle"), new("relativePath", "secret-needle.txt")]));
        Assert.AreEqual("JournalAcknowledgementFailed", safe.Code);
        Assert.AreEqual("80070005", safe.Fields["hresult"]);
        Assert.AreEqual(operation.ToString("D"), safe.Fields["operationId"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("needle", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ContentConfirmationDiagnosticsAcceptOnlyTypedReceiptFacts()
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "CloudFiles.Upload", "MutationOutcomeAmbiguous", DateTimeOffset.UtcNow,
            [new("confirmationOutcome", "ProtectionLost"), new("confirmationStage", "Reference"),
             new("nativeApplied", "false"), new("nativeVerified", "false"), new("projectionCommitted", "true"),
             new("confirmationOutcome", "token-secret-needle"), new("confirmationStage", "path-secret-needle"), new("nativeApplied", "password-secret-needle")]));
        Assert.AreEqual("ProtectionLost", safe.Fields["confirmationOutcome"]);
        Assert.AreEqual("Reference", safe.Fields["confirmationStage"]);
        Assert.AreEqual("False", safe.Fields["nativeApplied"]);
        Assert.AreEqual("True", safe.Fields["projectionCommitted"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("needle", StringComparison.Ordinal));
    }
}
