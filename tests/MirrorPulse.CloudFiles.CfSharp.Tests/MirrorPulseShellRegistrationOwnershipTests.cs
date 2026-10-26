using System.Runtime.Versioning;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseShellRegistrationOwnershipTests
{
    private static MirrorPulseShellRegistrationProfile Profile() => MirrorPulseShellSyncRootRegistrar.CreateProfile(
        new(Path.Combine(Path.GetTempPath(), "MirrorPulse-shell-owner", "sync"), "0.1.0", Guid.NewGuid(), [4, 8, 15, 16, 23, 42]),
        "S-1-5-21-123");

    private static MirrorPulseShellCloudFilesRoot Cloud(MirrorPulseShellRegistrationProfile profile) =>
        new(profile.SyncRootPath, 42, MirrorPulseSyncRootRegistrationService.ProviderName, profile.CloudFilesRootIdentity!);

    [TestMethod]
    public void ExactNonemptyProviderStillRequiresTheSameShellIdAndPathWithoutQueryingCloudFilesState()
    {
        var profile = Profile();
        MirrorPulseShellCloudFilesRoot Read() => throw new AssertFailedException("An exact nonempty provider needs no fallback.");
        MirrorPulseShellRegistrationOwnership.Require(profile, profile.RegistrationId, profile.SyncRootPath.ToUpperInvariant(), profile.ProviderId, Read);
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId + "-foreign", profile.SyncRootPath, profile.ProviderId, Read));
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId, profile.SyncRootPath + "-foreign", profile.ProviderId, Read));
    }

    [TestMethod]
    public void AForeignNonemptyProviderIsRefusedBeforeAnyAdditionalLookupEvenWithMatchingRootIdentity()
    {
        var profile = Profile();
        var foreign = Guid.NewGuid();
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId, profile.SyncRootPath, foreign,
            () => throw new AssertFailedException("A foreign provider cannot borrow Cloud Files ownership.")));
        StringAssert.Contains(failure.Message, $"expected={profile.ProviderId:D}");
        StringAssert.Contains(failure.Message, $"actual={foreign:D}");
    }

    [TestMethod]
    public void EmptyReadbackRequiresFreshMatchingPublicCloudFilesIdentityOnEveryInvocation()
    {
        var profile = Profile();
        var actual = Cloud(profile);
        int reads = 0;
        MirrorPulseShellCloudFilesRoot Read() { reads++; return actual; }
        MirrorPulseShellRegistrationOwnership.Require(profile, profile.RegistrationId, profile.SyncRootPath, Guid.Empty, Read);
        Assert.AreEqual(1, reads);
        actual = actual with { Identity = Convert.ToBase64String([99]) };
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId, profile.SyncRootPath, Guid.Empty, Read));
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    [DataRow("shell-id")]
    [DataRow("shell-path")]
    [DataRow("expected-provider")]
    [DataRow("missing-identity")]
    public void EmptyReadbackCannotInventOwnershipOrQueryAnUnselectedCloudFilesRoot(string damage)
    {
        var profile = Profile();
        var expected = damage switch
        {
            "expected-provider" => profile with { ProviderId = Guid.Empty },
            "missing-identity" => profile with { CloudFilesRootIdentity = null },
            _ => profile,
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellRegistrationOwnership.Require(expected,
            profile.RegistrationId + (damage == "shell-id" ? "-foreign" : ""),
            profile.SyncRootPath + (damage == "shell-path" ? "-foreign" : ""), Guid.Empty,
            () => throw new AssertFailedException("No fallback read is authorized for this candidate.")));
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("relative-path")]
    [DataRow("provider-name")]
    [DataRow("identity")]
    [DataRow("file-id")]
    [DataRow("absent")]
    public void EmptyReadbackRefusesEveryMissingOrForeignPublicCloudFilesFact(string damage)
    {
        var profile = Profile();
        var actual = Cloud(profile);
        actual = damage switch
        {
            "path" => actual with { Path = actual.Path + "-foreign" },
            "relative-path" => actual with { Path = "sync" },
            "provider-name" => actual with { ProviderName = "Another provider" },
            "identity" => actual with { Identity = Convert.ToBase64String([99]) },
            "file-id" => actual with { FileId = 0 },
            "absent" => null!,
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId, profile.SyncRootPath, Guid.Empty, () => actual));
    }

    [TestMethod]
    public void FreshRegistrationReadFailureIsPreservedAndCannotBeReplacedByCachedOrPathOnlyOwnership()
    {
        var profile = Profile();
        var original = new IOException("Fresh Cloud Files registration could not be read.");
        var failure = Assert.ThrowsExactly<IOException>(() => MirrorPulseShellRegistrationOwnership.Require(
            profile, profile.RegistrationId, profile.SyncRootPath, Guid.Empty, () => throw original));
        Assert.AreSame(original, failure);
    }
}
