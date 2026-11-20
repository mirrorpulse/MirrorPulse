using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task MovesFollowConfirmedIdentityChangesWithoutRewritingHistoricalCompletions(bool originalFile, bool acceptBeforeFirstMove)
    {
        using var fixture = new Fixture(); MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent first, second; MirrorPulseNamespaceMemberMoveCompletion completed;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (originalFile) setup = await WithOriginalMovableFileAsync(catalog, setup);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            first = setup.Move;
            if (acceptBeforeFirstMove)
            {
                var upload = await PrepareIdentityUploadAsync(catalog, setup, root, first.PreparedAt.AddSeconds(1));
                await CorrelateAsync(catalog, setup.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), upload.Proof.OperationId);
                first = WithAcceptedMoveIdentity(first, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), upload.Receipt.ObservedAt.AddSeconds(1));
            }
            completed = await CompleteMoveChainStepAsync(catalog, first);
            var next = await PrepareIdentityUploadAsync(catalog, setup, root, completed.VerifiedAt.AddSeconds(1));
            await CorrelateAsync(catalog, setup.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), next.Proof.OperationId);
            second = WithAcceptedMoveIdentity(ContinueMove(first, completed, first.Target, "Docs/accepted.txt"),
                await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), next.Receipt.ObservedAt.AddSeconds(1));
            await CompleteMoveChainStepAsync(catalog, second);
            Assert.AreEqual(completed, await catalog.ReadNamespaceMemberMoveCompletionAsync(first.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(first, await reopened.PrepareNamespaceMemberMoveAsync(first));
        Assert.AreEqual(completed, await reopened.CompleteNamespaceMemberMoveAsync(completed));
        Assert.AreEqual(second, await reopened.ReadNamespaceMemberMoveAsync(second.OperationId));
        Assert.AreEqual(new(setup.Member.MemberId, second.Target.ParentMemberId, second.Target.RelativePath, second.OperationId),
            await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberChildAsync(second.Target.ParentMemberId, "accepted.txt"));
        Assert.AreEqual(second.ItemId, (await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId)).ItemId);
        Assert.IsNull(await reopened.ReadPendingNamespaceMemberMoveAsync(second.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("stale")]
    [DataRow("missing-reference")]
    [DataRow("unknown-reference")]
    [DataRow("wrong-item")]
    [DataRow("wrong-remote")]
    [DataRow("before-confirmation")]
    [DataRow("before-previous-move")]
    public async Task MoveAdmissionRejectsUnprovedStaleOrReorderedAcceptedIdentities(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var first = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var observedAt = mismatch == "before-previous-move" ? setup.Member.RegisteredAt.AddSeconds(1) : first.VerifiedAt.AddSeconds(1);
        var upload = await PrepareIdentityUploadAsync(catalog, setup, root, observedAt);
        await CorrelateAsync(catalog, setup.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), upload.Proof.OperationId);
        var next = WithAcceptedMoveIdentity(ContinueMove(setup.Move, first, setup.Move.Target, "Docs/next.txt"),
            await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), first.VerifiedAt.AddSeconds(3));
        var altered = mismatch switch
        {
            "stale" => next with { IdentityAcceptanceOperationId = null, ItemId = setup.Move.ItemId, RemoteId = setup.Move.RemoteId },
            "missing-reference" => next with { IdentityAcceptanceOperationId = null },
            "unknown-reference" => next with { IdentityAcceptanceOperationId = Guid.NewGuid() },
            "wrong-item" => next with { ItemId = Guid.NewGuid() },
            "wrong-remote" => next with { RemoteId = "unproved:remote" },
            "before-confirmation" => next with { PreparedAt = upload.Receipt.ObservedAt.AddTicks(-1) },
            _ => next
        };
        if (mismatch == "unknown-reference") await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(next.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(next.RootId));
        Assert.AreEqual(setup.Move.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CompletedMoveOperationId);
        Assert.AreEqual(upload.Proof, await catalog.ReadContentAcceptanceProofAsync(upload.Proof.OperationId));
    }

    private static MirrorPulseNamespaceMemberMoveIntent WithAcceptedMoveIdentity(MirrorPulseNamespaceMemberMoveIntent move,
        MirrorPulseNamespaceMemberIdentity identity, DateTimeOffset preparedAt) => move with
        { IdentityAcceptanceOperationId = identity.AcceptanceOperationId, ItemId = identity.ItemId!.Value, RemoteId = identity.RemoteId!, PreparedAt = preparedAt };
}
