using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task DirectoryIdentityAdvancesOnlyAfterTypedConfirmationAndSurvivesTwoOwners()
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseDirectoryAcceptance first, second;
        MirrorPulseDirectoryConfirmationReceipt firstReceipt, secondReceipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog, directory: true);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            var original = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
            first = await PrepareDirectoryIdentityAsync(catalog, setup, root);
            Assert.AreEqual(original, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
            Assert.IsNull(await catalog.ReadDirectoryConfirmationAsync(first.OperationId));
            firstReceipt = DirectoryReceipt(first, setup.Member.RegisteredAt.AddSeconds(1));
            await catalog.RecordDirectoryConfirmationAsync(firstReceipt);
            Assert.IsNull(await catalog.ReadContentAcceptanceProofAsync(first.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceIdentityAcceptanceAsync(first.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(first.OperationId))!.State);
            await catalog.TransitionMutationAsync(first.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged);
            second = await PrepareDirectoryIdentityAsync(catalog, setup, root);
            secondReceipt = DirectoryReceipt(second, firstReceipt.ObservedAt.AddSeconds(1));
            await catalog.RecordDirectoryConfirmationAsync(secondReceipt);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(first, await reopened.ReadDirectoryAcceptanceAsync(first.OperationId));
        Assert.AreEqual(firstReceipt, await reopened.ReadDirectoryConfirmationAsync(first.OperationId));
        Assert.AreEqual(secondReceipt, await reopened.RecordDirectoryConfirmationAsync(secondReceipt));
        Assert.AreEqual(second, await reopened.PrepareDirectoryAcceptanceAsync(second));
        var current = await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        Assert.AreEqual(second.OperationId, current.AcceptanceOperationId);
        Assert.AreEqual(second.AcceptedItemId, current.ItemId); Assert.AreEqual(second.RemoteId, current.RemoteId);
        Assert.AreEqual(setup.Move.ItemId, current.OriginalItemId); Assert.AreEqual(setup.Move.RemoteId, current.OriginalRemoteId);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task ControlledDirectoryMoveRetainsAConfirmedDirectoryIdentityAcrossOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceMemberMoveIntent move;
        MirrorPulseNamespaceDirectoryMoveCompletion completion;
        MirrorPulseDirectoryAcceptance proof;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog, directory: true);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            proof = await PrepareDirectoryIdentityAsync(catalog, setup, root);
            var receipt = DirectoryReceipt(proof, setup.Member.RegisteredAt.AddSeconds(1));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Move with
            {
                IdentityAcceptanceOperationId = proof.OperationId,
                ItemId = proof.AcceptedItemId,
                RemoteId = proof.RemoteId
            }));
            await catalog.RecordDirectoryConfirmationAsync(receipt);
            move = WithAcceptedMoveIdentity(setup.Move, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId),
                receipt.ObservedAt.AddSeconds(1));
            completion = await PrepareDirectoryCompletionFactsAsync(catalog, setup with { Move = move });
            await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(move, await reopened.ReadNamespaceMemberMoveAsync(move.OperationId));
        Assert.AreEqual(completion.Root, (await reopened.ReadNamespaceDirectoryMoveCompletionAsync(move.OperationId))!.Root);
        var snapshot = await reopened.ReadNamespaceDirectoryMoveSnapshotAsync(move.OperationId);
        Assert.AreEqual(proof.OperationId, snapshot!.Entries.Single().Identity.AcceptanceOperationId);
        Assert.AreEqual(proof.AcceptedItemId, snapshot.Entries.Single().Identity.ItemId);
    }

    [TestMethod]
    [DataRow("file-member")]
    [DataRow("file-mutation")]
    [DataRow("content")]
    [DataRow("metadata")]
    [DataRow("revision")]
    [DataRow("binding")]
    [DataRow("previous")]
    [DataRow("instance")]
    [DataRow("root-key")]
    [DataRow("path")]
    [DataRow("position")]
    [DataRow("intent-hash")]
    [DataRow("executing")]
    [DataRow("conflict")]
    public async Task DirectoryProofRefusesUnacceptedFileForeignOrLaterFacts(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: mismatch != "file-member");
        var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var original = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var proof = await PrepareDirectoryIdentityAsync(catalog, setup, root, mismatch, save: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareDirectoryAcceptanceAsync(proof));
        Assert.IsNull(await catalog.ReadDirectoryAcceptanceAsync(proof.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("native")]
    [DataRow("projection")]
    [DataRow("object")]
    [DataRow("identity")]
    [DataRow("remote-id")]
    [DataRow("revision")]
    [DataRow("encoded")]
    [DataRow("time")]
    public async Task DirectoryConfirmationCannotPromotePartialOrDifferentNativeFacts(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var original = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var proof = await PrepareDirectoryIdentityAsync(catalog, setup, root);
        var receipt = DirectoryReceipt(proof, setup.Member.RegisteredAt.AddSeconds(1));
        receipt = mismatch switch
        {
            "native" => receipt with { NativeInSync = false },
            "projection" => receipt with { OfficialProjectionVerified = false },
            "object" => receipt with { LocalObject = receipt.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "identity" => receipt with { ObservedItemId = Guid.NewGuid() },
            "remote-id" => receipt with { ObservedRemoteId = "later" },
            "revision" => receipt with { ObservedRevision = "later" },
            "encoded" => receipt with { ObservedPlaceholderIdentity = "invalid" },
            "time" => receipt with { ObservedAt = setup.Member.RegisteredAt.AddSeconds(-1) },
            _ => throw new InvalidOperationException(),
        };
        if (mismatch is "native" or "projection" or "encoded")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordDirectoryConfirmationAsync(receipt));
        else if (mismatch == "time") await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordDirectoryConfirmationAsync(receipt));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordDirectoryConfirmationAsync(receipt));
        Assert.IsNull(await catalog.ReadDirectoryConfirmationAsync(proof.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(proof.OperationId))!.State);
    }

    [TestMethod]
    public async Task DirectoryIdentityConfirmationCannotForkReplayDifferentFactsOrBypassRollback()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var original = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var first = await PrepareDirectoryIdentityAsync(catalog, setup, root);
        var alternate = await PrepareDirectoryIdentityAsync(catalog, setup, root);
        var receipt = DirectoryReceipt(first, setup.Member.RegisteredAt.AddSeconds(1));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.RecordDirectoryConfirmationAsync(receipt, cancel.Token));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER reject_directory_confirmation BEFORE UPDATE ON directory_acceptances BEGIN SELECT RAISE(ABORT,'interrupted directory confirmation'); END;");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.RecordDirectoryConfirmationAsync(receipt));
        Assert.IsNull(await catalog.ReadDirectoryConfirmationAsync(first.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_directory_confirmation;");
        await catalog.RecordDirectoryConfirmationAsync(receipt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordDirectoryConfirmationAsync(
            DirectoryReceipt(alternate, receipt.ObservedAt.AddSeconds(1))));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordDirectoryConfirmationAsync(receipt with { ObservedAt = receipt.ObservedAt.AddSeconds(1) }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareDirectoryAcceptanceAsync(first with { RemoteId = "changed" }));
        Assert.IsNull(await catalog.ReadDirectoryConfirmationAsync(alternate.OperationId));
        Assert.AreEqual(first.OperationId, (await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId)).AcceptanceOperationId);
    }

    private static MirrorPulseDirectoryConfirmationReceipt DirectoryReceipt(MirrorPulseDirectoryAcceptance proof, DateTimeOffset observedAt) =>
        // Synthetic Core contract facts only. Native confirmation and original SDK ACK are tested at their own boundaries.
        new(proof.OperationId, proof.LocalObject, "AQID", proof.AcceptedItemId, proof.RemoteId, proof.AcceptedRevision, true, true, observedAt);

    private static async Task<MirrorPulseDirectoryAcceptance> PrepareDirectoryIdentityAsync(MirrorPulseProductCatalog catalog,
        MoveFixture setup, RootRegistration root, string? mismatch = null, bool save = true)
    {
        var original = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var location = await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId);
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), mismatch == "instance" ? InstanceId.New() : root.InstanceId,
            mismatch == "root-key" ? "another-root" : root.UniquenessKey,
            mismatch == "metadata" ? MirrorPulseWorkerChangeKind.MetadataUpdate : MirrorPulseWorkerChangeKind.Create,
            "new.txt", null, mismatch != "file-mutation", null, mismatch == "content" ? 4 : null,
            mismatch == "content" ? new string('B', 64) : null, MirrorPulseMutationOrigin.Journal);
        await catalog.PrepareMutationAsync(intent);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        if (mismatch != "executing")
            await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
        if (mismatch == "conflict")
            await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Conflict);
        var proof = new MirrorPulseDirectoryAcceptance(1, intent.OperationId, setup.Member.MemberId, root.RootId,
            mismatch == "binding" ? setup.Member.LocalObject with { LocalFileId = Guid.NewGuid() } : setup.Member.LocalObject,
            original.AcceptanceOperationId, mismatch == "previous" ? Guid.NewGuid() : original.ItemId!.Value, original.RemoteId!,
            Guid.NewGuid(), "directory:" + Guid.NewGuid(), mismatch == "revision" ? "later" : "accepted",
            mismatch == "path" ? "Docs/later" : location.RelativePath, mismatch == "position" ? Guid.NewGuid() : location.CurrentPositionOperationId,
            mismatch == "intent-hash" ? new string('F', 64) : MirrorPulseProductCatalog.ComputeDirectoryAcceptanceIntentSha256(intent));
        return save ? await catalog.PrepareDirectoryAcceptanceAsync(proof) : proof;
    }
}
