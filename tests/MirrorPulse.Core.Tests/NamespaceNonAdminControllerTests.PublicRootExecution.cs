namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    public Task ControllerCountsFourPublicRootRuntimeExecutionsOnlyForTheirExplicitSelection(string selection) =>
        RunAsync("root-public-" + selection, 120);
}
