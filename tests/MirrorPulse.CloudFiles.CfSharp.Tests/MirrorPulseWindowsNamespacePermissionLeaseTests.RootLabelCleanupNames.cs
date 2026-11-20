namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static readonly string[] OriginalRootLabelCleanupNames = ["Docs", "Renamed"];
    private static readonly string[] PublicRootLabelCleanupNames = ["Docs", "Renamed", "FinalLabel"];

    [TestMethod]
    public void RootLabelCleanupKeepsOriginalNamesAndItsOptionalLiteralLabel()
    {
        CollectionAssert.AreEqual(OriginalRootLabelCleanupNames, RootLabelCleanupNames(null));
        CollectionAssert.AreEqual(PublicRootLabelCleanupNames, RootLabelCleanupNames("FinalLabel"));
    }

    [TestMethod]
    [DataRow("../FinalLabel")]
    [DataRow("FinalLabel/child")]
    [DataRow("./FinalLabel")]
    [DataRow("foreign")]
    public void RootLabelCleanupRejectsOtherNamesBeforeAnyFixtureAccess(string additionalName) =>
        Assert.ThrowsExactly<ArgumentException>(() => RootLabelCleanupNames(additionalName));
}
