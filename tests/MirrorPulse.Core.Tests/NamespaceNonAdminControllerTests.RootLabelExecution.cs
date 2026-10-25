namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRequiresTheActualProtectedRootExecutionForAllFourteenStateFaultExecutions(string selection) =>
        RunAsync("root-execution-" + selection, 116);
}
