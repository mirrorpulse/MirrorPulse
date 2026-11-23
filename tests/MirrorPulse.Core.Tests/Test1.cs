using MirrorPulse.Core;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class ProductInfoTests
{
    private static readonly string ExpectedProductName = "MirrorPulse";

    [TestMethod]
    public void ProductNameIsMirrorPulse()
    {
        Assert.AreEqual(ExpectedProductName, ProductInfo.Name);
    }
}
