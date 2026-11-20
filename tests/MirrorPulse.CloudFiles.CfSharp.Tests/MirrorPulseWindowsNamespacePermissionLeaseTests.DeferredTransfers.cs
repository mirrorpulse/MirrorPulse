namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("DeferredP079")]
    public Task NativeP079SameInstanceTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification() =>
        RunNativeHostTransferAsync(deleteSource: false, selectedSameInstance: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("DeferredP079")]
    public Task NativeP079SameInstanceTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes() =>
        RunNativeHostTransferAsync(deleteSource: true, selectedSameInstance: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("DeferredP079")]
    public Task NativeP079SameInstanceTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences() =>
        RunNativeHostTransferAsync(deleteSource: true, retireLocal: true, selectedSameInstance: true);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("DeferredP079")]
    public async Task NativeP079SameInstanceTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners()
    {
        foreach (string fault in new[] { "before-native", "after-native", "after-receipt", "native-receipt-write", "name-settlement-write" })
            await RunNativeHostTransferAsync(deleteSource: true, retireLocal: true, retirementFault: fault, selectedSameInstance: true);
    }

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("DeferredP079")]
    public Task NativeP079SameInstanceTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners() =>
        RunNativeHostTransferAsync(deleteSource: true, retireLocal: true, completeOriginals: true, selectedSameInstance: true);
}
