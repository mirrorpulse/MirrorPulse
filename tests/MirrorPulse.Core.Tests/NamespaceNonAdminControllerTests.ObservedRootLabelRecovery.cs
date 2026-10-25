namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRequiresAllEightActualObservedRootRecoveryStateFaultExecutions(string selection) =>
        RunAsync("root-observed-" + selection, 117);
}
