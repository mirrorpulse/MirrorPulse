namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteFileDeletionSelectsDistinctSuccessorAndPreservesBothGenerationsAcrossOwners() =>
        RunOriginalRemoteDeleteGapAsync(false, false, false, recreate: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeOriginalRemoteEmptyDirectoryDeletionSelectsDistinctSuccessorAndPreservesBothGenerationsAcrossOwners() =>
        RunOriginalRemoteDeleteGapAsync(true, false, false, recreate: true);
}
