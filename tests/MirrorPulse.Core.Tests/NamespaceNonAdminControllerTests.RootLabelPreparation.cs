namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRequiresTheActualProtectedRootPreparationForBothStateExecutions(string selection) =>
        RunAsync("root-label-" + selection, 115);
}
