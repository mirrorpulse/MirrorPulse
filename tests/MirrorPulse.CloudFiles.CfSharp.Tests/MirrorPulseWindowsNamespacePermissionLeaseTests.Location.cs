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
            Assert.IsNotNull(before.RemoteId);
            byte[] bytes = await File.ReadAllBytesAsync(original.FullPath, token);
            var sourceMember = await catalog.ReadNamespaceMemberAsync(member.ParentMemberId!.Value, token);
            Assert.IsNotNull(sourceMember);
            var sourceProtection = await catalog.ReadLatestNamespacePermissionChangeAsync(sourceMember.OriginId, token);
            var childProtection = await catalog.ReadLatestNamespaceBirthProtectionAsync(member.OriginId, token);
            var targetProtection = await catalog.ReadLatestNamespaceBirthProtectionAsync(destinationParent.OriginId, token);
            Assert.IsNotNull(sourceProtection); Assert.IsNotNull(childProtection); Assert.IsNotNull(targetProtection);
            var intent = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), member.MemberId, childProtection.ProtectionId,
                member.RootId!.Value, member.LocalObject, before.ItemId.Value, before.RemoteId, member.OwnerSid, role.RoleSid.Value,
                new(sourceMember.MemberId, sourceMember.LocalObject, sourceProtection.Intent.OperationId, member.RelativePath),
                new(destinationParent.MemberId, destinationParent.LocalObject, targetProtection.ProtectionId,
                    destinationParent.RelativePath + "/member-location-probe.bin"), DateTimeOffset.UtcNow);
            await catalog.PrepareNamespaceMemberMoveAsync(intent, token);
            var fence = new MirrorPulseRemoteNamespaceFence(router, catalog);
            var root = router.Registrations.Single(registration => registration.RootId == intent.RootId);
            Assert.IsFalse(await fence.CanPollAsync(root, token));
            var pendingBatch = await new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true).ReadPendingAsync(token);
            Assert.IsFalse(pendingBatch.RequiresFullRescan);
            Assert.HasCount(0, pendingBatch.ReadyCommands);
            Assert.IsGreaterThan(0, pendingBatch.DeferredCount);
            CollectionAssert.IsSubsetOf(retainedJournalIds,
                (await WaitForBirthJournalAsync(feed, before.ItemId.Value, directoryItemId, token)).Select(change => change.OperationId).ToArray());
            await catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, DateTimeOffset.UtcNow), token);
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
            Assert.AreEqual(intent, await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, token));
            Assert.IsFalse(await fence.CanPollAsync(root, token));
            TestContext.WriteLine("ControlledMemberLocation: sharedAdmission=True; officialItemIdLookup=True; publicMoveTo=True; parentDiscoveredByFullBinding=True; nativeBindingAndBytes=True; originalRegistrationUnchanged=True; journalIdsRetained=True; pendingMemberMoveFencesPoll=True; pendingMemberMoveDefersJournal=True; hostIntegrated=False.");
        }, token);
    }
}
