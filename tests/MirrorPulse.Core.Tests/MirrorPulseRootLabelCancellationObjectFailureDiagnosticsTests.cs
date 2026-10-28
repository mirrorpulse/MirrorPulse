using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationObjectFailureDiagnosticsTests
{
    private static MirrorPulseCancellationObjectObservation Original => new(MirrorPulseCancellationObjectScope.Root,
        MirrorPulseCancellationObjectOrigin.ProtectedBirth, MirrorPulseCancellationDescriptorDifference.Principals, MirrorPulseCancellationRoleReference.Present);

    [TestMethod]
    public async Task RejectedObjectEmitsOnlyClosedValuesAndTheSameOriginalException()
    {
        var original = new InvalidDataException("PRIVATE_PATH PRIVATE_DACL PRIVATE_SID token=PRIVATE_SECRET");
        MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(original, Original);
        SafeLogEntry? logged = null;
        var caught = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => ValueTask.FromException<int>(original), entry => { logged = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.IsNotNull(logged); Assert.HasCount(9, logged.Fields);
        Assert.AreEqual("Root", logged.Fields["rootLabelCancellationObjectScope"]);
        Assert.AreEqual("ProtectedBirth", logged.Fields["rootLabelCancellationObjectOrigin"]);
        Assert.AreEqual("Principals", logged.Fields["rootLabelCancellationDescriptorDifference"]);
        Assert.AreEqual("Present", logged.Fields["rootLabelCancellationCurrentRoleReference"]);
        Assert.IsFalse(JsonSerializer.Serialize(logged).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OuterObservationCannotReplaceTheOriginalObjectClassification()
    {
        var original = new InvalidDataException();
        MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(original, Original);
        MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(original, Original with { Scope = MirrorPulseCancellationObjectScope.Anchor });
        SafeLogEntry? logged = null;
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => ValueTask.FromException<int>(original), entry => { logged = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreEqual("Root", logged!.Fields["rootLabelCancellationObjectScope"]);
    }

    [TestMethod]
    public async Task LoggingFailureCannotReplaceAnAlreadyClassifiedCancellationToken()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var original = new OperationCanceledException(stop.Token);
        MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(original, Original);
        var caught = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => ValueTask.FromException<int>(original), _ => ValueTask.FromException(new IOException("PRIVATE_OBSERVER"))).AsTask());
        Assert.AreSame(original, caught); Assert.AreEqual(stop.Token, caught.CancellationToken);
    }

    [TestMethod]
    public async Task UnclassifiedFailuresRetainOnlyTheirExistingFiveFields()
    {
        SafeLogEntry? logged = null;
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(Guid.NewGuid(),
            () => ValueTask.FromException<int>(new IOException()), entry => { logged = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.HasCount(5, logged!.Fields);
    }

    [TestMethod]
    [DataRow("scope")]
    [DataRow("origin")]
    [DataRow("difference")]
    [DataRow("role")]
    public void InvalidClosedObjectValuesCannotBeMarked(string field)
    {
        var value = field switch
        {
            "scope" => Original with { Scope = (MirrorPulseCancellationObjectScope)int.MaxValue },
            "origin" => Original with { Origin = (MirrorPulseCancellationObjectOrigin)int.MaxValue },
            "difference" => Original with { Difference = (MirrorPulseCancellationDescriptorDifference)int.MaxValue },
            _ => Original with { CurrentExecutionRoleReference = (MirrorPulseCancellationRoleReference)int.MaxValue }
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(new IOException(), value));
    }

    [TestMethod]
    public void UnknownAndPrivateFieldValuesAreOmitted()
    {
        foreach (string value in new[] { "PRIVATE_PATH", "1", "present", "Principals PRIVATE_SID" })
        {
            var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "host", "RootLabelCancellationFailed", DateTimeOffset.UtcNow,
                [new("rootLabelCancellationObjectScope", value), new("rootLabelCancellationObjectOrigin", value),
                 new("rootLabelCancellationDescriptorDifference", value), new("rootLabelCancellationCurrentRoleReference", value)]));
            Assert.IsFalse(safe.Fields.Keys.Any(key => key.StartsWith("rootLabelCancellation", StringComparison.Ordinal)));
            Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE_", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void NullClassificationInputsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(null!, Original));
        Assert.ThrowsExactly<ArgumentNullException>(() => MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(new IOException(), null!));
    }

    [TestMethod]
    public async Task EveryClosedObjectVocabularyValueSurvivesThePrivateExportPolicy()
    {
        foreach (var scope in Enum.GetValues<MirrorPulseCancellationObjectScope>())
            foreach (var origin in Enum.GetValues<MirrorPulseCancellationObjectOrigin>())
                foreach (var difference in Enum.GetValues<MirrorPulseCancellationDescriptorDifference>())
                    foreach (var role in Enum.GetValues<MirrorPulseCancellationRoleReference>())
                    {
                        var original = new InvalidDataException("PRIVATE_DACL PRIVATE_SID");
                        MirrorPulseRootLabelCancellationObjectDiagnostics.Mark(original, new(scope, origin, difference, role));
                        SafeLogEntry? logged = null;
                        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseRootLabelCancellationFailureDiagnostics.RunAsync<int>(Guid.NewGuid(),
                            () => ValueTask.FromException<int>(original), entry => { logged = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
                        Assert.HasCount(9, logged!.Fields);
                        Assert.AreEqual(scope.ToString(), logged.Fields["rootLabelCancellationObjectScope"]);
                        Assert.AreEqual(origin.ToString(), logged.Fields["rootLabelCancellationObjectOrigin"]);
                        Assert.AreEqual(difference.ToString(), logged.Fields["rootLabelCancellationDescriptorDifference"]);
                        Assert.AreEqual(role.ToString(), logged.Fields["rootLabelCancellationCurrentRoleReference"]);
                        Assert.IsFalse(JsonSerializer.Serialize(logged).Contains("PRIVATE_", StringComparison.Ordinal));
                    }
    }
}
