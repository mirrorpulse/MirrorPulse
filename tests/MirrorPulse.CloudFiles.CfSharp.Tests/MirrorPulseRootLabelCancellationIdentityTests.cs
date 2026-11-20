using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationIdentityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExactOriginalRootProofAllowsAnAbsentOrMatchingDurableProjection(bool projected)
    {
        Guid itemId = Guid.NewGuid();
        byte[] native = new CloudPlaceholderIdentity(itemId, "original-root", "original-revision").Encode();
        MirrorPulseRootLabelCancellationIdentity.RequireOriginal(itemId, projected ? itemId : null, native, native);
    }

    [TestMethod]
    [DataRow("projected-item")]
    [DataRow("native-item")]
    [DataRow("native-remote")]
    [DataRow("native-revision")]
    [DataRow("missing-native")]
    [DataRow("truncated-native")]
    [DataRow("missing-projection-altered-native")]
    [DataRow("missing-projection-missing-native")]
    public void OptionalProjectionCannotAdoptAnAlteredOrMissingOriginalProof(string change)
    {
        Guid itemId = Guid.NewGuid(); Guid? projected = itemId;
        byte[] expected = new CloudPlaceholderIdentity(itemId, "original-root", "original-revision").Encode();
        byte[] actual = change switch
        {
            "native-item" => new CloudPlaceholderIdentity(Guid.NewGuid(), "original-root", "original-revision").Encode(),
            "native-remote" or "missing-projection-altered-native" => new CloudPlaceholderIdentity(itemId, "different-root", "original-revision").Encode(),
            "native-revision" => new CloudPlaceholderIdentity(itemId, "original-root", "different-revision").Encode(),
            "missing-native" or "missing-projection-missing-native" => [],
            "truncated-native" => expected[..^1],
            _ => expected
        };
        if (change == "projected-item") projected = Guid.NewGuid();
        if (change.StartsWith("missing-projection-", StringComparison.Ordinal)) projected = null;
        var failure = Assert.ThrowsExactly<InvalidDataException>(() =>
            MirrorPulseRootLabelCancellationIdentity.RequireOriginal(itemId, projected, actual, expected));
        Assert.AreEqual("Unsealed cancellation lost the exact original public root identity.", failure.Message);
    }
}
