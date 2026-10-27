using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class BornRecoveryProgressDiagnosticTests
{
    [TestMethod]
    [TestCategory("BornRecoveryProgress")]
    [DataRow("NamespaceBornRecoveryHistoryBegin")]
    [DataRow("NamespaceBornRecoveryHistoryCompleted")]
    [DataRow("NamespaceBornRecoveryContextBegin")]
    [DataRow("NamespaceBornRecoveryContextCompleted")]
    [DataRow("NamespaceBornRecoveryGuardsBegin")]
    [DataRow("NamespaceBornRecoveryGuardsCompleted")]
    [DataRow("NamespaceBornRecoveryActionBegin")]
    [DataRow("NamespaceBornRecoveryActionCompleted")]
    [DataRow("NamespaceBornRecoveryScopeBegin")]
    [DataRow("NamespaceBornRecoveryScopeCompleted")]
    [DataRow("NamespaceBornRecoveryReadbackBegin")]
    [DataRow("NamespaceBornRecoveryReadbackCompleted")]
    [DataRow("NamespaceBornRecoveryCommitBegin")]
    [DataRow("NamespaceBornRecoveryCommitCompleted")]
    public async Task BornRecoveryExportsItsClosedStageAndRejectsPrivateOrInventedValues(string stage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-born-recovery-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new LocalRollingLogWriter(directory);
            await writer.WriteAsync(new(LogLevel.Information, "CloudFiles.Startup", "CloudFilesStartupProgress", DateTimeOffset.UtcNow,
                [new("startupStage", stage), new("startupSequence", "23"), new("startupElapsedMs", "4157"),
                 new("path", "private-path-needle"), new("ownerSid", "private-owner-needle"), new("credential", "private-token-needle")]));
            string json = await File.ReadAllTextAsync(writer.FilePath);
            using var stored = JsonDocument.Parse(json);
            var fields = stored.RootElement.GetProperty("Fields");
            Assert.IsTrue(fields.TryGetProperty("startupStage", out var actual), "The real rolling writer must retain the admitted recovery boundary.");
            Assert.AreEqual(stage, actual.GetString());
            Assert.AreEqual("23", fields.GetProperty("startupSequence").GetString());
            Assert.AreEqual("4157", fields.GetProperty("startupElapsedMs").GetString());
            Assert.IsFalse(json.Contains("needle", StringComparison.Ordinal));
            foreach (string rejected in new[] { stage + "/private-needle", "NamespaceBornRecovery999", "private-path-needle" })
            {
                var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Information, "CloudFiles.Startup", "CloudFilesStartupProgress", DateTimeOffset.UtcNow,
                    [new("startupStage", rejected), new("path", "private-path-needle")]));
                Assert.IsFalse(safe.Fields.ContainsKey("startupStage"));
                Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("needle", StringComparison.Ordinal));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
