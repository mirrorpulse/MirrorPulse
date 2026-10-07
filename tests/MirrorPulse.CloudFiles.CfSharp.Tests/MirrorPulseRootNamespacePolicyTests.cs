using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRootNamespacePolicyTests
{
    [TestMethod]
    public void ManagedRootDeletionIsDeniedWhileChildDeletionAndOtherRootsRemainAvailable()
    {
        string sync = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-policy", Guid.NewGuid().ToString("N"));
        InstanceId instance = InstanceId.New();
        RootRegistration Root(string key, string name, RootRegistrationState state) => AdapterRootRegistrationMapper.Map(
            AdapterId.Parse("example.roots"), instance, new AdapterRootDefinition(key, name, name, false), state,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(sync, [Root("docs", "Docs", RootRegistrationState.Active),
            Root("offline", "Offline", RootRegistrationState.Disabled)]);
        foreach (string path in new[] { sync, "", "Docs", Path.Combine(sync, "Docs"), "Offline", "Unknown", "../outside" })
            Assert.AreEqual(CloudProviderPolicyDecision.Deny, MirrorPulseRootNamespacePolicy.ApproveDelete(router, path), path);
        foreach (string path in new[] { "Docs/file.txt", "Docs/folder", "Offline/resident.txt", Path.Combine(sync, "Docs", "file.txt") })
            Assert.AreEqual(CloudProviderPolicyDecision.Allow, MirrorPulseRootNamespacePolicy.ApproveDelete(router, path), path);
    }
}
