namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [TestCategory("LegacyInitializerBudget")]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("count-only")]
    public Task ControllerIncludesBothOriginalInitializerExecutionsOnlyForItsExactBoundedSelection(string selection) =>
        RunAsync("legacy-initializer-" + selection, 123);
}
