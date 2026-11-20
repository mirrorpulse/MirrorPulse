namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("unrelated")]
    [DataRow("duplicate")]
    [DataRow("count-only")]
    public Task ControllerAddsOnlyTheActualDisabledRetirementDeadlineAndRetainsEveryPriorSelection(string selection) =>
        RunAsync("disabled-retirement-" + selection, 145);
}
