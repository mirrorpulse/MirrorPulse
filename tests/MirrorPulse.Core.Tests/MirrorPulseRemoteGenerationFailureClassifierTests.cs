using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRemoteGenerationFailureClassifierTests
{
    [TestMethod]
    public void PollStagesExportOnlyExactNamedValues()
    {
        foreach (string value in Enum.GetNames<MirrorPulseRemotePollStage>().Concat(["1", "PRIVATE_PATH", "Projection, SnapshotRead"]))
        {
            var entry = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
                [new("remotePollStage", value)]));
            bool expected = Enum.GetNames<MirrorPulseRemotePollStage>().Contains(value, StringComparer.Ordinal);
            Assert.AreEqual(expected, entry.Fields.ContainsKey("remotePollStage"));
            if (expected) Assert.AreEqual(value, entry.Fields["remotePollStage"]);
        }
    }

    [TestMethod]
    [DataRow("The indexed current generation disagrees with its immutable address.", MirrorPulseRemoteFailureBoundary.GenerationHistory)]
    [DataRow("A source generation lost its original transfer.", MirrorPulseRemoteFailureBoundary.GenerationHistory)]
    [DataRow("The selected generation does not encode this original backend address and stable root.", MirrorPulseRemoteFailureBoundary.GenerationAddress)]
    [DataRow("The captured public address does not identify this backend object and stable root.", MirrorPulseRemoteFailureBoundary.PollProjection)]
    [DataRow("The retained candidate changed independently of its intent.", MirrorPulseRemoteFailureBoundary.PollProjection)]
    [DataRow("The Adapter batch does not follow its durable CfSharp cursor.", MirrorPulseRemoteFailureBoundary.PollCursor)]
    [DataRow("The placeholder identity belongs to another Adapter root.", MirrorPulseRemoteFailureBoundary.RootIdentityMapping)]
    [DataRow("A missing member cannot invent a remote deletion admission.", MirrorPulseRemoteFailureBoundary.RetirementAdmission)]
    [DataRow("The original SDK birth item projection does not match its admitted identity and path.", MirrorPulseRemoteFailureBoundary.BirthItemProjection)]
    public void GenerationFailuresExportOnlyTheirClosedBoundary(string message, MirrorPulseRemoteFailureBoundary boundary)
    {
        var original = new InvalidDataException(message);
        Assert.AreEqual(boundary, MirrorPulseRemoteFailureClassifier.ReadBoundary(original));
        var entry = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
            [new("remoteFailureBoundary", boundary.ToString()), new("message", message), new("stack", "PRIVATE_STACK")]));
        Assert.AreEqual(boundary.ToString(), entry.Fields["remoteFailureBoundary"]);
        string exported = JsonSerializer.Serialize(entry);
        Assert.IsFalse(exported.Contains(message, StringComparison.Ordinal));
        Assert.IsFalse(exported.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.AreEqual(MirrorPulseRemoteFailureBoundary.Unknown,
            MirrorPulseRemoteFailureClassifier.ReadBoundary(new InvalidDataException(message + " PRIVATE_PATH")));
        Assert.AreEqual(message, original.Message);
    }
}
