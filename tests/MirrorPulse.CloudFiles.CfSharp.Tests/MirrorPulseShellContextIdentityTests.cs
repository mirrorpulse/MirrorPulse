using System.Runtime.Versioning;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseShellContextIdentityTests
{
    private static MirrorPulseShellRegistrationProfile Profile()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-context-tests", "sync");
        return MirrorPulseShellSyncRootRegistrar.CreateProfile(new(root, "0.1.0", Guid.NewGuid(), [1, 3, 5, 7]), "S-1-5-21-123");
    }

    [TestMethod]
    public void ContextPreservesConfiguredIdentityAcrossLabelChangesAndReturnedBufferEdits()
    {
        var profile = Profile();
        byte[] first = MirrorPulseShellRegistrationOwnership.CreateContextIdentity(profile);
        CollectionAssert.AreEqual(new byte[] { 1, 3, 5, 7 }, first);
        first[0] = 99;
        byte[] next = MirrorPulseShellRegistrationOwnership.CreateContextIdentity(profile with { DisplayName = "Personal drive" });
        CollectionAssert.AreEqual(new byte[] { 1, 3, 5, 7 }, next);
        Assert.AreNotSame(first, next);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void ContextCannotBeInventedFromThePathWhenDefinitionBytesAreAbsent(string? missing)
    {
        var profile = Profile() with { CloudFilesRootIdentity = missing };
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellRegistrationOwnership.CreateContextIdentity(profile));
    }

    [TestMethod]
    public void ContextRejectsDamagedRetainedBytesBeforeCallingWindows()
    {
        var profile = Profile() with { CloudFilesRootIdentity = "not base64" };
        Assert.ThrowsExactly<FormatException>(() => MirrorPulseShellRegistrationOwnership.CreateContextIdentity(profile));
    }
}
