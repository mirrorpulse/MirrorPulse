using System.Text.Json;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelSubtreeFailureDiagnosticsTests
{
    [TestMethod]
    [DataRow(MirrorPulseRootLabelExecutionStage.SubtreePublicReconciliation)]
    [DataRow(MirrorPulseRootLabelExecutionStage.SubtreeCatalogSeal)]
    public async Task SubtreeFailureKeepsItsSpecificBoundaryAndOriginalFailureWithoutPrivateText(MirrorPulseRootLabelExecutionStage stage)
    {
        Guid operation = Guid.NewGuid();
        var original = new InvalidOperationException("PRIVATE_PATH password=PRIVATE_SECRET");
        SafeLogEntry? observed = null;
        var caught = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseRootLabelExecutionFailureDiagnostics.RunAsync(operation,
            () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync(MirrorPulseRootLabelExecutionStage.SubtreePreparation,
                () => MirrorPulseRootLabelExecutionFailureDiagnostics.AtStageAsync<int>(stage, () => ValueTask.FromException<int>(original))),
            entry => { observed = SafeDiagnosticPolicy.Sanitize(entry); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreSame(original, caught); Assert.IsNotNull(observed);
        Assert.AreEqual(stage, MirrorPulseRootLabelExecutionFailureDiagnostics.Stage(caught));
        Assert.AreEqual(stage.ToString(), observed.Fields["rootLabelExecutionStage"]);
        Assert.AreEqual(operation.ToString("D"), observed.Fields["operationId"]);
        Assert.AreEqual("80131509", observed.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(observed).Contains("PRIVATE_", StringComparison.Ordinal));
    }
}
