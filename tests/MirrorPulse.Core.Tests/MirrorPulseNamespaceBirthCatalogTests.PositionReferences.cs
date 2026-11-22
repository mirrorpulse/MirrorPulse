using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ExplicitCompletedSourcePositionSurvivesReopenAndKeepsDirectMoveHistory()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent next;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            var completed = await CompleteMoveChainStepAsync(first, setup.Move);
            next = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/next.txt") with
            { SourcePositionOperationId = completed.OperationId };
            await first.PrepareNamespaceMemberMoveAsync(next);
            Assert.AreEqual(next, await first.ValidateNamespaceMemberMoveExecutionAsync(next.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(next, await reopened.ReadNamespaceMemberMoveAsync(next.OperationId));
        await CompleteMoveChainStepAsync(reopened, next);
        var position = await reopened.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, next.OperationId);
        Assert.IsNotNull(position);
        Assert.AreEqual(setup.Move.OperationId, position.PreviousPositionOperationId);
        Assert.AreEqual(next.OperationId, position.CompletedMoveOperationId);
        Assert.AreEqual(next.OperationId, (await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("source-member")]
    [DataRow("source-parent")]
    [DataRow("target-parent")]
    [DataRow("uncompleted-source")]
    [DataRow("missing-source")]
    public async Task MovePositionReferencesCannotBorrowAnotherMemberOrAnUncompletedOperation(string substitution)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var next = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/next.txt");
        var other = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/other.txt" });
        var otherMember = BornMember(other, setup.ParentMember);
        await catalog.RegisterNamespaceMemberAsync(otherMember);
        var foreignMove = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), otherMember.MemberId,
            other.Protection.ProtectionId, other.Birth.RootId, otherMember.LocalObject, other.Plan.ItemId, other.Plan.RemoteId,
            otherMember.OwnerSid, other.Protection.RoleSid,
            new(setup.ParentMember.MemberId, setup.ParentMember.LocalObject, setup.ParentProof.OperationId, otherMember.RelativePath),
            new(setup.ParentMember.MemberId, setup.ParentMember.LocalObject, setup.ParentProof.OperationId, "Docs/other-moved.txt"),
            otherMember.RegisteredAt.AddSeconds(1));
        if (substitution == "uncompleted-source") await catalog.PrepareNamespaceMemberMoveAsync(foreignMove);
        else if (substitution == "source-member") await CompleteMoveChainStepAsync(catalog, foreignMove);
        next = next with { PreparedAt = new[] { next.PreparedAt, foreignMove.PreparedAt.AddSeconds(10) }.Max() };
        var altered = substitution switch
        {
            "source-parent" => next with { Source = next.Source with { ParentPositionOperationId = completed.OperationId } },
            "target-parent" => next with { Target = next.Target with { ParentPositionOperationId = completed.OperationId } },
            "missing-source" => next with { SourcePositionOperationId = Guid.NewGuid() },
            _ => next with { SourcePositionOperationId = foreignMove.OperationId }
        };
        if (substitution == "missing-source")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(next.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, next.OperationId));
        Assert.AreEqual(completed.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("source-parent")]
    [DataRow("target-parent")]
    public async Task MovePositionReferencesRejectSelfCyclesBeforeAnyAdmission(string endpoint)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var altered = endpoint switch
        {
            "source-parent" => setup.Move with { Source = setup.Move.Source with { ParentPositionOperationId = setup.Move.OperationId } },
            "target-parent" => setup.Move with { Target = setup.Move.Target with { ParentPositionOperationId = setup.Move.OperationId } },
            _ => setup.Move with { SourcePositionOperationId = setup.Move.OperationId }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }
}
