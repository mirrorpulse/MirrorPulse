using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRemoteFailureClassifierTests
{
    [TestMethod]
    [DataRow("The remote birth identity is already owned by another original object or birth.", MirrorPulseRemoteFailureBoundary.BirthIdentityOwnership)]
    [DataRow("A new birth cannot adopt an original protected file identity.", MirrorPulseRemoteFailureBoundary.BirthPlanIdentityOwnership)]
    [DataRow("Remote changes must wait for managed root namespace recovery.", MirrorPulseRemoteFailureBoundary.NamespaceRecovery)]
    [DataRow("PRIVATE_PATH password=PRIVATE_VALUE", MirrorPulseRemoteFailureBoundary.Unknown)]
    [DataRow("The remote birth identity is already owned by another original object or birth. PRIVATE_PATH", MirrorPulseRemoteFailureBoundary.Unknown)]
    public void OnlyFixedMessagesIdentifyAClosedBoundaryAndNoMessageIsExported(string message, MirrorPulseRemoteFailureBoundary expected)
    {
        var failure = new InvalidOperationException(message);
        Assert.AreEqual(expected, MirrorPulseRemoteFailureClassifier.ReadBoundary(failure));
        var sanitized = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
            [new("remoteFailureBoundary", expected.ToString()), new("message", message), new("stack", "PRIVATE_STACK")]));
        Assert.AreEqual(expected.ToString(), sanitized.Fields["remoteFailureBoundary"]);
        Assert.IsFalse(JsonSerializer.Serialize(sanitized).Contains("PRIVATE", StringComparison.Ordinal));
        Assert.AreEqual("Remote polling failed.", sanitized.Message);
    }

    [TestMethod]
    public void OnlyNamedRemoteFailureBoundariesSurviveExport()
    {
        foreach (var boundary in Enum.GetValues<MirrorPulseRemoteFailureBoundary>())
        {
            var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
                [new("remoteFailureBoundary", boundary.ToString())]));
            Assert.AreEqual(boundary.ToString(), safe.Fields["remoteFailureBoundary"]);
        }
        foreach (string value in new[] { "1", "Unknown, BirthStarted", "PRIVATE_PATH", "BirthStarted/PRIVATE_VALUE" })
        {
            var rejected = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
                [new("remoteFailureBoundary", value)]));
            Assert.IsFalse(rejected.Fields.ContainsKey("remoteFailureBoundary"));
        }
    }
}
