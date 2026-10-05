using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Packaging;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CurrentUserAdapterPathProviderTests
{
    [TestMethod]
    [DataRow("1.2.3")]
    [DataRow("1.2.3.0")]
    [DataRow("1.2.3-preview.10")]
    public void InstallationPathSeparatesAdapterVersionAndInstallId(string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "mirrorpulse-adapters");
        var provider = new CurrentUserAdapterPathProvider(root);
        var adapterId = AdapterId.Parse("example.webdav");
        var installId = InstallId.New();

        var path = provider.GetInstallationDirectory(adapterId, version, installId);

        Assert.AreEqual(Path.Combine(root, "example.webdav", version, installId.ToString()), path);
    }

    [TestMethod]
    public void PathProviderRejectsTraversalLikeVersions()
    {
        var provider = new CurrentUserAdapterPathProvider(Path.Combine(Path.GetTempPath(), "mirrorpulse-adapters"));

        Assert.ThrowsExactly<ArgumentException>(() =>
            provider.GetVersionDirectory(AdapterId.Parse("example.webdav"), "../1.2.3"));
    }
}
