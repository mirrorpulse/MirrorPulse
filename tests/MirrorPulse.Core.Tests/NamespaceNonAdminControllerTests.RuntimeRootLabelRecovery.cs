namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRequiresAllSixActualRuntimeRootRecoveryStateFaultExecutions(string selection) =>
        RunAsync("root-runtime-" + selection, 118);
}
