namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("selected")]
    [DataRow("missing")]
    [DataRow("unrelated")]
    [DataRow("duplicate")]
    [DataRow("wrong-case")]
    [DataRow("count-only")]
    [DataRow("over-limit")]
    public Task DirectoryControllerRetainsEveryOriginalDeadlineAndRefusesIncompleteOrUnboundedSelections(string selection) =>
        RunAsync("directory-journal-" + selection, 163);
}
