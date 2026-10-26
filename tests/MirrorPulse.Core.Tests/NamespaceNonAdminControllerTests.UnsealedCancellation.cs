namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRetainsBothUnsealedCancellationDeadlinesOnlyForTheirExplicitSelection(string selection) =>
        RunAsync("unsealed-cancellation-" + selection, 122);
}
