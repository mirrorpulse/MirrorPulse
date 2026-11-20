using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteRejectionRetainsTheActualPreDispatchRevisionWhenTheRequestIsStaleAcrossOwners(bool absentRevision)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveStart start;
        string? actualRevision = absentRevision ? null : "actual-official-before-dispatch";
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRemoteRejectableMoveAsync(catalog, start: false);
            start = new(2, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)) { OriginalOfficialRemoteRevision = actualRevision };
            Assert.AreEqual(start, await catalog.RecordNamespaceMemberMoveStartAsync(start));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await reopened.ReadNamespaceMemberMoveStartAsync(start.OperationId));
        Assert.AreNotEqual(setup.Move.RemoteEntry!.PreviousRemoteRevision, start.OriginalOfficialRemoteRevision);
        var rejection = RemoteRejection(setup) with { OfficialRemoteRevision = actualRevision };
        Assert.AreEqual(rejection, await reopened.RecordNamespaceRemoteMoveRejectionAsync(rejection));
        Assert.IsNull(await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Move.Source.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
        Assert.AreEqual(start, await reopened.ReadNamespaceMemberMoveStartAsync(start.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveCompletionAsync(start.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task ARemoteStartCannotReplaceItsOriginalObservedRevisionWithTheRequestsExpectation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog, start: false);
        var start = new MirrorPulseNamespaceMemberMoveStart(2, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1))
        { OriginalOfficialRemoteRevision = "actual" };
        await catalog.RecordNamespaceMemberMoveStartAsync(start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveStartAsync(
            start with { OriginalOfficialRemoteRevision = setup.Move.RemoteEntry!.PreviousRemoteRevision }));
        Assert.AreEqual(start, await catalog.ReadNamespaceMemberMoveStartAsync(start.OperationId));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteMoveRejectionAsync(RemoteRejection(setup)));
        Assert.IsNull(await catalog.ReadNamespaceRemoteMoveRejectionAsync(start.OperationId));
    }

    [TestMethod]
    [DataRow("legacy-with-revision")]
    [DataRow("future-version")]
    [DataRow("nul-revision")]
    [DataRow("oversized-revision")]
    [DataRow("ordinary")]
    public async Task RemoteProjectionStartRejectsIncompleteOrUnownedEvidenceBeforePersistence(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog, start: false, remote: invalid != "ordinary");
        var start = new MirrorPulseNamespaceMemberMoveStart(2, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1))
        { OriginalOfficialRemoteRevision = "actual" };
        start = invalid switch
        {
            "legacy-with-revision" => start with { Version = 1 },
            "future-version" => start with { Version = 3 },
            "nul-revision" => start with { OriginalOfficialRemoteRevision = "bad\0revision" },
            "oversized-revision" => start with { OriginalOfficialRemoteRevision = new string('x', 16_385) },
            _ => start,
        };
        if (invalid == "ordinary")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveStartAsync(start));
        else await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceMemberMoveStartAsync(start));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(start.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task Schema61UpgradePreservesALegacyStartWithoutInventingAnOfficialRevision()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceMemberMoveStart original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteRejectableMoveAsync(catalog);
            original = (await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId))!;
            Assert.AreEqual(1, original.Version); Assert.IsNull(original.OriginalOfficialRemoteRevision);
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=61;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await reopened.ReadNamespaceMemberMoveStartAsync(original.OperationId));
        Assert.IsNull((await reopened.ReadNamespaceMemberMoveStartAsync(original.OperationId))!.OriginalOfficialRemoteRevision);
    }
}
