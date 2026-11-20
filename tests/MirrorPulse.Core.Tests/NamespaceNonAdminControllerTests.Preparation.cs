namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(112)]
    [DataRow(113)]
    public Task ControllerIncludesBothOriginalPreparationAvailabilityDeadlines(int count) =>
        RunAsync("preparation-selected", count);

    [TestMethod]
    public Task ExpandedControllerSelectionRequiresTheActualAdditionalPreparationCase() =>
        RunAsync("preparation-missing", 113);
}
