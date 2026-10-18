using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRemoteTreeRetirementCoordinatorTests
{
    [TestMethod]
    [DataRow("instance")]
    [DataRow("ordinal")]
    [DataRow("identity")]
    [DataRow("file")]
    [DataRow("preservation")]
    [DataRow("budget")]
    public void IncompatibleDirectoryDeleteInputCannotReachSdkOrCatalog(string invalid)
    {
        var instance = InstanceId.New(); var owner = instance; var options = CloudRemoteApplyOptions.Default;
        var batch = new CloudRemoteChangeBatch(instance + "/original-tree", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, "original-directory", "v2", invalid == "file" ? CloudItemKind.File : CloudItemKind.Directory,
                "Docs\\tree", invalid == "identity" ? null : Guid.NewGuid(), previousRemoteRevision: "v1", cursorAfter: new byte[] { 2 })], new byte[] { 2 });
        if (invalid == "instance") owner = InstanceId.New();
        if (invalid == "preservation") options = options with { PreserveUnsynchronizedLocalContent = false };
        if (invalid == "budget") options = options with { MaximumEntries = 0 };
        if (invalid is "instance" or "ordinal") Assert.ThrowsExactly<InvalidDataException>(() =>
            MirrorPulseRemoteTreeRetirementCoordinator.RequireChange(owner, batch, invalid == "ordinal" ? 1 : 0, options));
        else Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseRemoteTreeRetirementCoordinator.RequireChange(owner, batch, 0, options));
    }
}
