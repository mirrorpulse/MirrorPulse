namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [TestCategory("BornRootCancellationBudget")]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("count-only")]
    public Task ControllerRetainsTheOriginalCatalogAndAddsOnlyBothInheritedBornRootCancellationScopes(string selection) =>
        RunAsync("born-root-cancellation-" + selection, 124);
}
