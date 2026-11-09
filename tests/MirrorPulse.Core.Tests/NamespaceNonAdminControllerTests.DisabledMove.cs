namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("unrelated")]
    [DataRow("duplicate")]
    [DataRow("count-only")]
    public Task ControllerAddsOnlyTheActualDisabledMoveDeadlineAndRetainsEveryPriorSelection(string selection) =>
        RunAsync("disabled-move-" + selection, 144);
}
