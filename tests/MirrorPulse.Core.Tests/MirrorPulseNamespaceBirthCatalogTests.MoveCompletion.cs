using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task FreshMoveCompletionTransfersTheCurrentNameAndReleasesTheRootFence()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup);
        var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        var completion = MoveCompletion(native, projection);

        Assert.AreEqual(completion, await catalog.CompleteNamespaceMemberMoveAsync(completion));
        Assert.AreEqual(completion, await catalog.ReadNamespaceMemberMoveCompletionAsync(completion.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(setup.Member,
            await catalog.ReadNamespaceMemberChildAsync(setup.Move.Target.ParentMemberId, "moved.txt"));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(completion, await catalog.CompleteNamespaceMemberMoveAsync(completion));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task MoveStatusReportsDurablePhasesWithoutChangingAdmission()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var prepared = await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId);
        Assert.IsNull(prepared);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var admitted = await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId);
        Assert.IsNotNull(admitted);
        Assert.AreEqual("Prepared", admitted.Phase);
        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
        Assert.AreEqual("Started", (await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId))!.Phase);
        var native = MoveObservation(setup);
        var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        Assert.AreEqual("Observed", (await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId))!.Phase);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        Assert.AreEqual("Projected", (await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId))!.Phase);
        await catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection));
        var completed = await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId);
        Assert.IsNotNull(completed);
        Assert.AreEqual("Completed", completed.Phase);
        Assert.IsTrue(completed.CurrentNameCommitted);
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task SingleItemDirectoryProjectionCannotReleaseTheSubtreeFence()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection)));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.Move.Target.ParentMemberId, "moved.txt"));
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("path")]
    [DataRow("child-dacl")]
    [DataRow("parent-dacl")]
    [DataRow("stale-time")]
    public async Task MoveCompletionRejectsStaleOrSubstitutedFreshFactsWithoutReleasingTheFence(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        var completion = MoveCompletion(native, projection);
        var changed = mismatch switch
        {
            "identity" => completion with { ItemId = Guid.NewGuid() },
            "path" => completion with { RelativePath = setup.Move.Source.RelativePath },
            "child-dacl" => completion with { Child = completion.Child with { Dacl = setup.Parent.OriginalDacl } },
            "parent-dacl" => completion with { Parent = completion.Parent with { Dacl = setup.Parent.OriginalDacl } },
            _ => completion with
            {
                VerifiedAt = projection.ObservedAt,
                Child = completion.Child with { ObservedAt = native.Child.ObservedAt },
                Parent = completion.Parent with { ObservedAt = native.Parent.ObservedAt }
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceMemberMoveAsync(changed));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(completion.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    public async Task MoveCompletionInsertionFailureRollsBackNameHandoverAndRetainsRecoveryReservation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        await ExecuteSqlAsync(fixture.Paths,
            "CREATE TRIGGER reject_move_completion BEFORE INSERT ON namespace_member_move_completions BEGIN SELECT RAISE(ABORT,'controlled completion interruption'); END;");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection)));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.Move.Target.ParentMemberId, "moved.txt"));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_move_completion;");
        await catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection));
    }

    private static MirrorPulseNamespaceMemberMoveCompletion MoveCompletion(
        MirrorPulseNamespaceMemberMoveObservation native, MirrorPulseNamespaceMemberMoveProjection projection) =>
        new(1, native.OperationId, native.ItemId, native.RemoteId, native.RelativePath, native.IsDirectory,
            projection.ObservedAt.AddSeconds(2), native.Child with { ObservedAt = projection.ObservedAt.AddMilliseconds(10) },
            native.Parent with { ObservedAt = projection.ObservedAt.AddMilliseconds(11) }, projection.ProjectedRemoteRevision);
}
