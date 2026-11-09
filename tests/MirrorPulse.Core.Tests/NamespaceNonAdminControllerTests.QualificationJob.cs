namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    public Task ExplicitRescanJournalGateRetainsItsNativeDeadlineAndEveryHistoricalSelection() =>
        RunAsync("journal-before-rescan-selected");

    [TestMethod]
    public Task DisabledQualificationJobRetainsItsOriginalSetupAndArtifactMarginBeyondTheActualNativeStep() =>
        RunAsync("disabled-job-deadline");

    [TestMethod]
    public Task DisabledHostQualificationJobRetainsTheSignedSetupAndArtifactMarginBeyondItsActualFixture() =>
        RunAsync("disabled-host-job-deadline");
}
