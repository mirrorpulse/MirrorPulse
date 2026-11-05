namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("unrelated")]
    [DataRow("duplicate")]
    [DataRow("count-only")]
    [DataRow("restoration-missing")]
    [DataRow("restoration-unrelated")]
    public Task ControllerRetainsEveryOriginalDeadlineAndAddsOnlyActualKeepRemoteExecutions(string selection) =>
        RunAsync("keep-remote-" + selection, 133);
}
