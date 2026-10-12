using System.Security.AccessControl;
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
        CloudFile original = fileSystem.GetFile(member.RelativePath);
        CloudFile target = fileSystem.GetFile(destinationParent.RelativePath + "/member-location-probe.bin");
        byte[] bytes = await File.ReadAllBytesAsync(original.FullPath, token);
        var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
        var inspector = new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, role, actor);
        var selected = await inspector.InspectAsync(member.RelativePath, false, token);
        Assert.AreEqual(member.MemberId, selected.MemberId);
        Assert.AreEqual(member.ParentMemberId, selected.ActualParentMemberId);
        var selectedParent = await inspector.InspectAsync(destinationParent.RelativePath, true, token);
        Assert.AreEqual(destinationParent.MemberId, selectedParent.MemberId);
        Guid operationId = Guid.NewGuid();
        MirrorPulseNamespaceMemberMoveIntent intent = await coordinator.PrepareAsync(operationId, selected.MemberId,
            selectedParent.MemberId, "member-location-probe.bin", token);
        Assert.AreEqual(intent, await coordinator.PrepareAsync(operationId, member.MemberId, destinationParent.MemberId, "member-location-probe.bin", token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.PrepareAsync(operationId, member.MemberId,
            destinationParent.MemberId, "another-name.bin", token).AsTask());
        Assert.IsTrue((await original.InspectAsync(token)).Exists, "Preparation must not move the native object.");
        Assert.IsFalse((await target.InspectAsync(token)).Exists);
        await RunNativeMemberAdmissionAsync(actor, role, async () =>
        {
            var pendingBatch = await new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true).ReadPendingAsync(token);
            Assert.IsFalse(pendingBatch.RequiresFullRescan);
            Assert.HasCount(0, pendingBatch.ReadyCommands);
            Assert.IsGreaterThan(0, pendingBatch.DeferredCount);
        }, token);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(intent.OperationId, canceled.Token).AsTask());
        }
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token));
        // A changed live descriptor must fail before a durable start or any native movement.
        await role.RunNamespaceOperationAsync(async () =>
        {
            await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(original, router, token);
            var before = await guard.InspectAsync(token);
            try
            {
                string changedDacl = new RawSecurityDescriptor($"D:P(A;;FA;;;{role.OwnerSid.Value})(A;;FA;;;{role.RoleSid.Value})")
                    .GetSddlForm(AccessControlSections.Access);
                Assert.AreNotEqual(before.Dacl, changedDacl, "The negative case must change the live descriptor.");
                await guard.ApplyDaclAsync(changedDacl, token);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.ExecuteAsync(intent.OperationId, token).AsTask());
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(original.FullPath, token));
            }
            finally { await guard.ApplyDaclAsync(before.Dacl, CancellationToken.None); }
        });

        MirrorPulseNamespaceMemberMoveStart? firstStart = null;
        MirrorPulseNamespaceMemberMoveObservation? firstNative = null;
        MirrorPulseNamespaceMemberMoveProjection? firstProjection = null;
        foreach (var stopAt in new[] { MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove,
            MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation, MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection })
        {
            var interrupted = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor,
                (checkpoint, durable) =>
                {
                    Assert.IsFalse(durable.CanBeCanceled, "Post-native recovery must drain independently of the client token.");
                    if (checkpoint == stopAt) throw new IOException("Injected product move interruption.");
                    return ValueTask.CompletedTask;
                });
            var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ExecuteAsync(intent.OperationId, token).AsTask());
            StringAssert.Contains(failure.Message, "Injected product move interruption.");
            var start = await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token);
            Assert.IsNotNull(start); firstStart ??= start; Assert.AreEqual(firstStart, start);
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(intent.OperationId, token));
            Assert.AreEqual(intent, await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, token));
            Assert.IsTrue((await catalog.ReadNamespaceRecoveryRootsAsync(token)).Contains(intent.RootId));
            Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(member.ParentMemberId!.Value, "born.bin", token));
            Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(destinationParent.MemberId, "member-location-probe.bin", token));
            Assert.IsFalse((await original.InspectAsync(token)).Exists);
            var native = await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, token);
            var projection = await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, token);
            if (stopAt == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove) Assert.IsNull(native);
            else { Assert.IsNotNull(native); firstNative ??= native; Assert.AreEqual(firstNative, native); }
            if (stopAt == MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection) { Assert.IsNotNull(projection); firstProjection = projection; }
            else Assert.IsNull(projection);
            var actual = await target.InspectAsync(token);
            Assert.AreEqual(member.LocalObject, new MirrorPulseLocalFileBinding(actual.LocalBinding!.VolumeSerialNumber, actual.LocalBinding.SyncRootFileId, actual.LocalBinding.LocalFileId));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target.FullPath, token));
            CollectionAssert.IsSubsetOf(retainedJournalIds,
                (await WaitForBirthJournalAsync(feed, intent.ItemId, directoryItemId, token)).Select(change => change.OperationId).ToArray());
        }

        var completion = await coordinator.ExecuteAsync(intent.OperationId, token);
        Assert.AreEqual(completion, await coordinator.ExecuteAsync(intent.OperationId, token));
        Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, token));
        Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, token));
        Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, token));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, token));
        Assert.IsFalse((await catalog.ReadNamespaceRecoveryRootsAsync(token)).Contains(intent.RootId));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(member.ParentMemberId!.Value, "born.bin", token));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(destinationParent.MemberId, "member-location-probe.bin", token));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, token));
        var movedMember = await inspector.InspectAsync(target.RelativePath, false, token);
        Assert.AreEqual(member.MemberId, movedMember.MemberId);
        Assert.AreEqual(destinationParent.MemberId, movedMember.ActualParentMemberId);
        Assert.AreEqual(selected.NativeItemId, movedMember.NativeItemId);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target.FullPath, token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(target.FullPath), token));
        CollectionAssert.IsSubsetOf(retainedJournalIds,
            (await WaitForBirthJournalAsync(feed, intent.ItemId, directoryItemId, token)).Select(change => change.OperationId).ToArray());
        TestContext.WriteLine("ControlledMemberLocation: sharedAdmission=True; officialItemIdLookup=True; publicMoveTo=True; nativeBindingAndBytes=True; originalRegistrationUnchanged=True; journalIdsRetained=True; pendingMemberMoveFencesPoll=True; pendingMemberMoveDefersJournal=True; productionMoveCoordinator=True; productionMovePreparation=True; nativeMemberInspection=True; preparationReplay=True; changedDescriptorRejectedBeforeStart=True; nativeObservationProjectionInterruptionsRecovered=True; immutableStartAndFacts=True; currentNameCommitted=True; completedReplay=True; hostIntegrated=False.");
    }
}
