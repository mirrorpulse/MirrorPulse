namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerCountsSixActualCancellationAndRefusalExecutionsOnlyForTheirExplicitSelection(string selection) =>
        RunAsync("root-cancellation-" + selection, 121);
}
