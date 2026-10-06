using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterPackageVersionTests
{
    [TestMethod]
    [DataRow("0.2.0", false, false)]
    [DataRow("0.2.0-preview.1", true, false)]
    [DataRow("1.2.3.0", false, true)]
    [DataRow("1.2.3.4", false, true)]
    [DataRow("2147483647.2147483647.2147483647-preview.2147483647", true, false)]
    public void CanonicalIdentityIsPreserved(string text, bool preview, bool legacy)
    {
        Assert.IsTrue(AdapterPackageVersion.TryParse(text, out var version));
        Assert.AreEqual(text, version.Value);
        Assert.AreEqual(text, version.ToString());
        Assert.AreEqual(preview, version.IsPreview);
        Assert.AreEqual(legacy, version.IsLegacyFourPart);
        Assert.AreEqual(version, AdapterPackageVersion.Parse(text));
    }

    [TestMethod]
    public void ComparisonKeepsLegacyIdentityAndOrdersPreviewsNumericallyBeforeStable()
    {
        string[] ordered = ["0.2.0-preview.2", "0.2.0-preview.10", "0.2.0", "0.2.0.0", "0.2.0.1", "0.2.1-preview.1", "0.2.1"];
        var versions = ordered.Select(AdapterPackageVersion.Parse).ToArray();
        for (int index = 1; index < versions.Length; index++)
        {
            Assert.IsLessThan(0, versions[index - 1].CompareTo(versions[index]));
            Assert.IsGreaterThan(0, versions[index].CompareTo(versions[index - 1]));
            Assert.IsTrue(versions[index - 1] < versions[index]);
            Assert.IsTrue(versions[index] >= versions[index - 1]);
        }

        Assert.AreNotEqual(AdapterPackageVersion.Parse("0.2.0"), AdapterPackageVersion.Parse("0.2.0.0"));
        Assert.IsTrue(versions[0] == AdapterPackageVersion.Parse(ordered[0]));
        Assert.IsTrue(versions[0] != versions[1]);
        Assert.IsTrue(null < versions[0]);
        Assert.IsTrue(versions[0] > null);
        Assert.IsTrue((AdapterPackageVersion?)null <= null);
        CollectionAssert.AreEqual(ordered, versions.Reverse().Order().Select(value => value.Value).ToArray());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1.2")]
    [DataRow("01.2.3")]
    [DataRow("1.02.3")]
    [DataRow("1.2.3.04")]
    [DataRow("1.2.3.4-preview.1")]
    [DataRow("1.2.3-preview.0")]
    [DataRow("1.2.3-preview.01")]
    [DataRow("1.2.3-Preview.1")]
    [DataRow("1.2.3-preview.2147483648")]
    [DataRow("2147483648.2.3")]
    [DataRow("1.2.3.2147483648")]
    [DataRow("1.2.3+metadata")]
    [DataRow("1.2.3\n")]
    [DataRow(" 1.2.3")]
    [DataRow("1.2.3 ")]
    [DataRow("v1.2.3")]
    [DataRow("../1.2.3")]
    [DataRow("1.2.3;throw 1")]
    [DataRow("１.2.3")]
    public void AmbiguousUnsafeOrUnsupportedIdentityIsRejected(string? text)
    {
        Assert.IsFalse(AdapterPackageVersion.TryParse(text, out var version));
        Assert.IsNull(version);
        Assert.ThrowsExactly<FormatException>(() => AdapterPackageVersion.Parse(text!));
    }
}
