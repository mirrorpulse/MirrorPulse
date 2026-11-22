namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    public Task ControllerRetainsEveryOriginalDeadlineAndAllReceivedDeleteRecoveryRows() =>
        RunAsync("received-delete-selected");
}
