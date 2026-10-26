using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelFailureDiagnosticsTests
{
    [TestMethod]
    [DataRow("New root preparation requires its current unfenced Host audit.", MirrorPulseRootLabelFailureBoundary.CurrentAuditFenced)]
    [DataRow("Root notification acceptance requires the same complete unfenced audited inventory.", MirrorPulseRootLabelFailureBoundary.CurrentAuditFenced)]
    [DataRow("Root readmission requires a complete unfenced audit under the live Host role.", MirrorPulseRootLabelFailureBoundary.CurrentAuditFenced)]
    [DataRow("The current Host has no live startup guard.", MirrorPulseRootLabelFailureBoundary.LiveGuard)]
    [DataRow("The original root notification already has immutable local-policy ownership.", MirrorPulseRootLabelFailureBoundary.OriginalOwnership)]
    [DataRow("The original remote batch must converge before preparing a root rename.", MirrorPulseRootLabelFailureBoundary.PendingRemoteBatch)]
    [DataRow("The managed root has a pending namespace transition.", MirrorPulseRootLabelFailureBoundary.PendingTransition)]
    [DataRow("The managed root cannot be renamed in its current state.", MirrorPulseRootLabelFailureBoundary.UnavailableRoot)]
    [DataRow("Root journal policy requires its completed original protected Label and whole-subtree placement.", MirrorPulseRootLabelFailureBoundary.CompletedPlacement)]
    [DataRow("PRIVATE_PATH token=PRIVATE_SECRET", MirrorPulseRootLabelFailureBoundary.Unknown)]
    public void RootLabelGuardClassificationRequiresAnExactProductDescription(string message, MirrorPulseRootLabelFailureBoundary expected)
    {
        Assert.AreEqual(expected, MirrorPulseRootLabelFailureDiagnostics.Classify(new InvalidOperationException(message)));
        Assert.AreEqual(MirrorPulseRootLabelFailureBoundary.Unknown, MirrorPulseRootLabelFailureDiagnostics.Classify(new IOException(message)));
    }

    [TestMethod]
    [DataRow("Root metadata admission requires its current public native root identity.", MirrorPulseRootLabelFailureBoundary.NativeIdentity)]
    [DataRow("Root preparation lost its live sync-root anchor.", MirrorPulseRootLabelFailureBoundary.LiveGuard)]
    [DataRow("The accepted root observation lost its original metadata owner.", MirrorPulseRootLabelFailureBoundary.OriginalOwnership)]
    [DataRow("The current root placement lost its complete atomic subtree receipt.", MirrorPulseRootLabelFailureBoundary.CompletedPlacement)]
    [DataRow("The complete actual local inventory differs from its current protected membership.", MirrorPulseRootLabelFailureBoundary.CurrentTree)]
    public void RootLabelDataGuardClassificationKeepsNativeIdentityOwnershipAndInventorySeparate(string message, MirrorPulseRootLabelFailureBoundary expected) =>
        Assert.AreEqual(expected, MirrorPulseRootLabelFailureDiagnostics.Classify(new InvalidDataException(message)));

    [TestMethod]
    public async Task RootLabelFailureRetainsTheSameOriginalExceptionAndOnlyFixedSanitizedFields()
    {
        var original = new InvalidOperationException("New root preparation requires its current unfenced Host audit.");
        Guid root = Guid.NewGuid(); SafeLogEntry? observed = null;
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            MirrorPulseRootLabelFailureDiagnostics.RunPreparationAsync<int>(root, () => throw original,
                entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, failure); Assert.IsNotNull(observed);
        Assert.AreEqual("RootLabelPreparationFailed", observed.Code); Assert.AreEqual(root.ToString("D"), observed.Fields["rootId"]);
        Assert.AreEqual("CurrentAuditFenced", observed.Fields["rootLabelFailureBoundary"]);
        Assert.AreEqual("80131509", observed.Fields["hresult"]);
        Assert.DoesNotContain(original.Message, JsonSerializer.Serialize(observed));
    }

    [TestMethod]
    public async Task DiagnosticWriterFailureCannotReplaceTheOriginalPreparationFailureOrCancellation()
    {
        foreach (Exception original in new Exception[] { new InvalidDataException("PRIVATE_PATH"), new OperationCanceledException("PRIVATE_SECRET") })
        {
            Exception? retained = null;
            try
            {
                await MirrorPulseRootLabelFailureDiagnostics.RunPreparationAsync<int>(Guid.NewGuid(), () => throw original,
                    _ => throw new IOException("PRIVATE_LOG_FAILURE"));
            }
            catch (Exception failure) { retained = failure; }
            Assert.AreSame(original, retained);
        }
    }

    [TestMethod]
    public async Task SuccessfulPreparationReturnsTheOriginalResultWithoutEmittingFailure()
    {
        var expected = new object(); int writes = 0;
        object actual = await MirrorPulseRootLabelFailureDiagnostics.RunPreparationAsync(Guid.NewGuid(), () => ValueTask.FromResult(expected),
            _ => { writes++; return ValueTask.CompletedTask; });
        Assert.AreSame(expected, actual); Assert.AreEqual(0, writes);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("CurrentAuditFenced, LiveGuard")]
    [DataRow("CurrentAuditFenced/PRIVATE_PATH")]
    [DataRow("PRIVATE_SECRET")]
    public void RootLabelFailureFieldsRejectNumericCombinedAndPrivateValues(string value)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "host", "RootLabelPreparationFailed", DateTimeOffset.UtcNow,
            [new("rootLabelFailureBoundary", value), new("exceptionMessage", "PRIVATE_SECRET")]));
        Assert.IsFalse(safe.Fields.ContainsKey("rootLabelFailureBoundary"));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(safe));
    }

    [TestMethod]
    public async Task PreparationStageRetainsTheDeepestOriginalFailureWithoutReplacingItOrLoggingPrivateFields()
    {
        var original = new InvalidOperationException("PRIVATE_PATH token=PRIVATE_SECRET");
        SafeLogEntry? observed = null;
        var retained = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            MirrorPulseRootLabelFailureDiagnostics.RunPreparationAsync<int>(Guid.NewGuid(),
                () => MirrorPulseRootLabelFailureDiagnostics.AtPreparationStageAsync(MirrorPulseRootLabelPreparationStage.InstanceLibrary,
                    () => MirrorPulseRootLabelFailureDiagnostics.AtPreparationStageAsync<int>(MirrorPulseRootLabelPreparationStage.CurrentTreeAudit, () => throw original)),
                entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, retained); Assert.IsNotNull(observed);
        Assert.AreEqual("Unknown", observed.Fields["rootLabelFailureBoundary"]);
        Assert.AreEqual("CurrentTreeAudit", observed.Fields["rootLabelPreparationStage"]);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(observed));
    }

    [TestMethod]
    public async Task PreparationStagePreservesCancellationAndSuccessfulResults()
    {
        var original = new OperationCanceledException();
        var retained = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            MirrorPulseRootLabelFailureDiagnostics.AtPreparationStageAsync<int>(MirrorPulseRootLabelPreparationStage.RootWorkDrain, () => throw original).AsTask());
        Assert.AreSame(original, retained);
        Assert.AreEqual(MirrorPulseRootLabelPreparationStage.RootWorkDrain, MirrorPulseRootLabelFailureDiagnostics.PreparationStage(retained));
        Assert.AreEqual(7, await MirrorPulseRootLabelFailureDiagnostics.AtPreparationStageAsync(MirrorPulseRootLabelPreparationStage.PublicProof, () => ValueTask.FromResult(7)));
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("CurrentTreeAudit, PublicProof")]
    [DataRow("PRIVATE_PATH")]
    public void PreparationStageRejectsArbitraryOrNoncanonicalDiagnosticValues(string value)
    {
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Warning, "host", "RootLabelPreparationFailed", DateTimeOffset.UtcNow,
            [new("rootLabelPreparationStage", value)]));
        Assert.IsFalse(safe.Fields.ContainsKey("rootLabelPreparationStage"));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(safe));
    }
}
