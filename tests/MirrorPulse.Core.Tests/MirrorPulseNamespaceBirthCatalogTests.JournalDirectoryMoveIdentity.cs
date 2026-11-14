using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task Schema108IdentityUpgradeRetainsConfirmedDirectoryAndFilePredecessors()
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryConfirmationReceipt directory;
        MirrorPulseNamespaceIdentityAcceptance file;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var folder = await PrepareMovableMemberAsync(first, directory: true);
            var root = await AddIdentityTopologyAsync(first, folder.Move.RootId);
            var directoryProof = await PrepareDirectoryIdentityAsync(first, folder, root);
            directory = await first.RecordDirectoryConfirmationAsync(DirectoryReceipt(directoryProof, folder.Member.RegisteredAt.AddSeconds(1)));
            var born = await AddBornAsync(first, NestedBirth(folder.Born, "child.txt", false));
            var child = BornMember(born, folder.Member);
            await first.RegisterNamespaceMemberAsync(child);
            var upload = await PrepareIdentityUploadAsync(first, folder with { Member = child }, root, child.RegisteredAt.AddSeconds(1),
                relativePath: "new.txt/child.txt");
            file = await CorrelateAsync(first, child.MemberId, await first.ReadNamespaceMemberIdentityAsync(child.MemberId),
                upload.Proof.OperationId);
        }
        // Restore the exact pre-Move columns and indexes while preserving the actual legacy proof rows.
        await ExecuteSqlAsync(fixture.Paths, """
            DROP INDEX ux_directory_identity_first;
            DROP INDEX ux_directory_identity_move;
            ALTER TABLE directory_acceptances DROP COLUMN previous_move_id;
            CREATE UNIQUE INDEX ux_directory_identity_first ON directory_acceptances(member_id) WHERE previous_operation_id IS NULL AND receipt IS NOT NULL;
            DROP INDEX ux_namespace_identity_first;
            DROP INDEX ux_namespace_identity_move;
            ALTER TABLE namespace_member_identity_acceptances DROP COLUMN previous_move_id;
            CREATE UNIQUE INDEX ux_namespace_identity_first ON namespace_member_identity_acceptances(member_id) WHERE previous_operation_id IS NULL;
            DROP TABLE journal_directory_move_projections;
            DROP TABLE journal_directory_moves;
            PRAGMA user_version=108;
            """);
        await using var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(directory, await upgraded.ReadDirectoryConfirmationAsync(directory.OperationId));
        Assert.AreEqual(file, await upgraded.ReadNamespaceIdentityAcceptanceAsync(file.Proof.OperationId));
    }

    [TestMethod]
    public async Task JournalDirectoryMoveIdentityPrecedesLaterFileAndDirectoryConfirmationsAcrossOwners()
    {
        using var fixture = new Fixture();
        DirectorySettlementFixture setup;
        RootRegistration root;
        Guid operationId = Guid.NewGuid();
        MirrorPulseDirectoryMoveTarget[] targets;
        DateTimeOffset projectedAt;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareDirectorySettlementAsync(first, movedChild: true);
            root = await AddIdentityTopologyAsync(first, setup.Move.Move.RootId);
            await first.CompleteNamespaceDirectoryMoveAsync(setup.Completion);
            var snapshot = await first.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Completion.OperationId);
            Assert.IsNotNull(snapshot);
            var originals = snapshot.Entries.Zip(setup.Completion.Entries, (entry, result) =>
                new MirrorPulseJournalDirectoryMoveMember(entry.MemberId, entry.LocalObject, entry.RelativePath,
                    result.Completion.RelativePath, entry.IsDirectory, result.Completion.ItemId, result.Completion.RemoteId,
                    result.Completion.OfficialRemoteRevision, result.Native.NativeRemoteRevision, entry.Identity.AcceptanceOperationId)).ToArray();
            var admission = new MirrorPulseJournalDirectoryMoveAdmission(operationId, 1, new string('A', 64),
                setup.Completion.OperationId, root.RootId, root.InstanceId, root.UniquenessKey, originals);
            var intent = new MirrorPulseMutationIntent(operationId, root.InstanceId, root.UniquenessKey, MirrorPulseWorkerChangeKind.Move,
                "moved.txt", "new.txt", true, admission.Root.OriginalRevision, null, null, MirrorPulseMutationOrigin.Journal);
            await first.StartJournalDirectoryMoveAsync(admission, intent, default);
            CollectionAssert.AreEqual(new[] { root.RootId }, (await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            var pending = (await first.ReadNamespacePendingOperationsAsync(root.RootId)).Single();
            Assert.AreEqual(new MirrorPulseNamespacePendingOperation(operationId, root.RootId, MirrorPulseNamespacePendingOperationKind.JournalDirectoryMove), pending);
            Assert.IsEmpty(await first.ReadNamespacePendingOperationsAsync(RootId.New()));
            Assert.AreEqual(pending, (await first.ReadPendingJournalDirectoryMovesAsync(null, 1, default)).Single());
            Assert.IsEmpty(await first.ReadPendingJournalDirectoryMovesAsync(operationId, 1, default));
            await first.AcceptJournalDirectoryMoveAsync(operationId, admission.Root.OriginalRevision!, default);
            targets = originals.Select(member => new MirrorPulseDirectoryMoveTarget(member.MemberId, Guid.NewGuid(),
                "target:" + member.TargetPath, member.OriginalRevision)).ToArray();
            await first.SaveJournalDirectoryMoveTargetsAsync(operationId, targets, default);
            Assert.AreEqual(originals[0].OriginalItemId, (await first.ReadNamespaceMemberIdentityAsync(originals[0].MemberId)).ItemId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.RecordJournalDirectoryMoveProjectionAsync(
                operationId, 0, new(targets[0], setup.Completion.Root.VerifiedAt.AddTicks(-1)), default));
            projectedAt = setup.Completion.Root.VerifiedAt.AddSeconds(1);
            for (int ordinal = 0; ordinal < targets.Length; ordinal++)
            {
                var prior = await first.ReadNamespaceMemberIdentityAsync(targets[ordinal].MemberId);
                Assert.AreEqual(originals[ordinal].OriginalItemId, prior.ItemId);
                await first.RecordJournalDirectoryMoveProjectionAsync(operationId, ordinal, new(targets[ordinal], projectedAt), default);
                var projected = await first.ReadNamespaceMemberIdentityAsync(targets[ordinal].MemberId);
                Assert.AreEqual(targets[ordinal].ItemId, projected.ItemId);
                Assert.AreEqual(operationId, projected.AcceptanceOperationId);
            }
            await first.BeginJournalDirectoryMoveAcknowledgementAsync(operationId, default);
            await first.CompleteJournalDirectoryMoveAcknowledgementAsync(operationId, default);
            Assert.IsEmpty(await first.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsEmpty(await first.ReadPendingJournalDirectoryMovesAsync(null, 4, default));
        }

        MirrorPulseNamespaceIdentityAcceptance uploadIdentity;
        MirrorPulseDirectoryAcceptance directoryProof;
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            foreach (var target in targets)
            {
                var identity = await second.ReadNamespaceMemberIdentityAsync(target.MemberId);
                Assert.AreEqual(target.ItemId, identity.ItemId);
                Assert.AreEqual(target.RemoteId, identity.RemoteId);
                Assert.AreEqual(operationId, identity.AcceptanceOperationId);
                Assert.AreEqual(setup.Completion.Entries.Single(entry => entry.MemberId == target.MemberId).Completion.ItemId, identity.OriginalItemId);
            }
            Assert.IsNull(await second.ReadContentAcceptanceProofAsync(operationId));
            Assert.IsNull(await second.ReadDirectoryConfirmationAsync(operationId));

            var childIdentity = await second.ReadNamespaceMemberIdentityAsync(setup.Child.MemberId);
            var upload = await PrepareIdentityUploadAsync(second, setup.Move with { Member = setup.Child }, root,
                projectedAt.AddSeconds(1), relativePath: "moved.txt/renamed.txt");
            uploadIdentity = await CorrelateAsync(second, setup.Child.MemberId, childIdentity, upload.Proof.OperationId);
            Assert.AreEqual(operationId, uploadIdentity.PreviousAcceptanceOperationId);

            directoryProof = await PrepareDirectoryIdentityAsync(second, setup.Move, root, workerPath: "moved.txt");
            await second.RecordDirectoryConfirmationAsync(DirectoryReceipt(directoryProof, projectedAt.AddSeconds(2)));
            Assert.AreEqual(operationId, directoryProof.PreviousAcceptanceOperationId);

            var rootIdentity = await second.ReadNamespaceMemberIdentityAsync(setup.Move.Member.MemberId);
            var next = ContinueMove(setup.Move.Move, setup.Completion.Root, setup.Move.Move.Target, "Docs/again");
            next = WithAcceptedMoveIdentity(next, rootIdentity, projectedAt.AddSeconds(3));
            await second.PrepareNamespaceMemberMoveAsync(next);
            var nextSnapshot = await second.ReadNamespaceDirectoryMoveSnapshotAsync(next.OperationId);
            Assert.IsNotNull(nextSnapshot);
            Assert.AreEqual(uploadIdentity.Proof.OperationId, nextSnapshot.Entries.Single(entry => entry.MemberId == setup.Child.MemberId).Identity.AcceptanceOperationId);
            Assert.AreEqual(directoryProof.OperationId, nextSnapshot.Entries[0].Identity.AcceptanceOperationId);
            Assert.AreEqual(setup.Completion.Root, await second.ReadNamespaceMemberMoveCompletionAsync(setup.Completion.OperationId));
        }

        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(uploadIdentity, await third.ReadNamespaceIdentityAcceptanceAsync(uploadIdentity.Proof.OperationId));
        Assert.AreEqual(directoryProof.AcceptedItemId, (await third.ReadNamespaceMemberIdentityAsync(setup.Move.Member.MemberId)).ItemId);
        Assert.AreEqual(uploadIdentity.Proof.AcceptedItemId, (await third.ReadNamespaceMemberIdentityAsync(setup.Child.MemberId)).ItemId);
    }
}
