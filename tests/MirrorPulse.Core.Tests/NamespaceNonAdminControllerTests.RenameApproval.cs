namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerRequiresTheSecondExplicitCaseForItsOriginalCallbackStateDeadlines(string selection) =>
        RunAsync("approval-" + selection, 114);
}
