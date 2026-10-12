using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private async Task VerifyNativeMemberLocationAfterPublicMoveAsync(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        MirrorPulseNamespaceActor actor, MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberRegistration destinationParent, CloudLocalChangeFeed feed,
        Guid[] retainedJournalIds, Guid directoryItemId, CancellationToken token)
    {
        await RunNativeMemberAdmissionAsync(actor, role, async () =>
        {
            CloudFile original = fileSystem.GetFile(member.RelativePath);
            CloudDirectory sourceParent = original.Parent!;
            CloudDirectory destination = fileSystem.GetDirectory(destinationParent.RelativePath);
            var before = await original.InspectAsync(token);
            Assert.IsNotNull(before.LocalBinding);
            Assert.IsNotNull(before.ItemId);
            byte[] bytes = await File.ReadAllBytesAsync(original.FullPath, token);
            // Exercise CfSharp's public move and durable projection, not a replacement path graph.
            CloudItemMoveResult moved = await original.MoveToAsync(destination, "member-location-probe.bin", cancellationToken: token);
            try
            {
                var location = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore, member.MemberId, actor, token);
                Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.Projected, location.State);
                Assert.AreEqual(moved.Item.RelativePath.Replace('\\', '/'), location.RelativePath?.Replace('\\', '/'));
                Assert.AreEqual(before.ItemId, location.ItemId);
                var after = await moved.Item.InspectAsync(token);
                Assert.AreEqual(before.LocalBinding, after.LocalBinding);
                var binding = after.LocalBinding!;
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberByBindingAsync(
                    new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), token));
                await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(destination, router, token);
                var parentFacts = await parentLease.InspectAsync(token);
                Assert.AreEqual(destinationParent, await catalog.ReadNamespaceMemberByBindingAsync(parentFacts.LocalObject, token));
                Assert.AreEqual(member.OwnerSid, parentFacts.OwnerSid);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(moved.Item.FullPath, token));
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, token));
                CollectionAssert.IsSubsetOf(retainedJournalIds,
                    (await WaitForBirthJournalAsync(feed, before.ItemId.Value, directoryItemId, token)).Select(change => change.OperationId).ToArray());
            }
            finally
            {
                // Restore this owned probe after cancellation as well; no product name ownership is changed.
                await moved.Item.MoveToAsync(sourceParent, Path.GetFileName(original.FullPath), cancellationToken: CancellationToken.None);
            }
            var restored = await MirrorPulseNamespaceMemberLocator.ResolveAsync(catalog, state.OpenStore, member.MemberId, actor, token);
            Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.Projected, restored.State);
            Assert.AreEqual(member.RelativePath, restored.RelativePath?.Replace('\\', '/'));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(original.FullPath, token));
            Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(member.ParentMemberId!.Value, Path.GetFileName(original.FullPath), token));
            TestContext.WriteLine("ControlledMemberLocation: sharedAdmission=True; officialItemIdLookup=True; publicMoveTo=True; parentDiscoveredByFullBinding=True; nativeBindingAndBytes=True; originalRegistrationUnchanged=True; journalIdsRetained=True; hostIntegrated=False.");
        }, token);
    }
}
