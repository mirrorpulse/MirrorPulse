namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerCountsAllSixRootAdmissionCutsOnlyForTheExplicitOriginalSelection(string selection) =>
        RunAsync("root-admission-" + selection, 119);
}
