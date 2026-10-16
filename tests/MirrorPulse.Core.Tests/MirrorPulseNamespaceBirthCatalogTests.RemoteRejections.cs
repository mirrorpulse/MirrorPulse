using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteRejectionReleasesOnlyItsAdmissionAndRetainsOriginalFactsAcrossOwners(bool directory)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceRemoteMoveRejection rejected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRemoteRejectableMoveAsync(catalog, directory);
            rejected = RemoteRejection(setup);
            Assert.AreEqual(rejected, await catalog.RecordNamespaceRemoteMoveRejectionAsync(rejected));
            Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.AreEqual("Rejected", (await catalog.ReadNamespaceMemberMoveStatusAsync(setup.Move.OperationId))!.Phase);
            Assert.AreEqual(setup.Move.Source.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(setup.Move.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(rejected, await reopened.ReadNamespaceRemoteMoveRejectionAsync(rejected.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadNamespaceMemberMoveAsync(rejected.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadNamespaceRemoteMoveAsync(setup.Move.RemoteEntry!.InstanceId,
            setup.Move.RemoteEntry.BatchId, setup.Move.RemoteEntry.Ordinal));
        Assert.IsFalse(await reopened.CanResumeNamespaceRemoteMoveAsync(setup.Move));
        Assert.AreEqual(rejected, await reopened.RecordNamespaceRemoteMoveRejectionAsync(rejected));
        Assert.IsNotNull(await reopened.ReadNamespaceMemberMoveStartAsync(rejected.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ValidateNamespaceMemberMoveExecutionAsync(rejected.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RecordNamespaceRemoteMoveRejectionAsync(rejected with { ConflictId = Guid.NewGuid() }));
        var peer = setup.Move with { OperationId = Guid.NewGuid(), RemoteEntry = null, Version = 1, PreparedAt = rejected.VerifiedAt.AddSeconds(1) };
        Assert.AreEqual(peer, await reopened.PrepareNamespaceMemberMoveAsync(peer));
        Assert.AreEqual(rejected, await reopened.RecordNamespaceRemoteMoveRejectionAsync(rejected));
        Assert.AreEqual(peer, await reopened.ReadPendingNamespaceMemberMoveAsync(peer.RootId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RejectedSuccessorDoesNotConsumeOrEnterTheCompletedMoveChain()
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveIntent retry;
        MirrorPulseNamespaceRemoteMoveRejection rejected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
            var attempt = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/rejected.txt") with
            { Version = 2, RemoteEntry = RemoteMoveReference() with { PreviousRemoteRevision = completed.OfficialRemoteRevision } };
            setup = setup with { Move = attempt }; await StartMoveAsync(catalog, setup);
            rejected = RemoteRejection(setup);
            await catalog.RecordNamespaceRemoteMoveRejectionAsync(rejected);
            Assert.AreEqual(completed.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(attempt.MemberId)).CompletedMoveOperationId);
            retry = attempt with { OperationId = Guid.NewGuid(), Version = 1, RemoteEntry = null, PreparedAt = rejected.VerifiedAt.AddSeconds(1) };
            await CompleteMoveChainStepAsync(catalog, retry);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(rejected, await reopened.ReadNamespaceRemoteMoveRejectionAsync(rejected.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadNamespaceMemberMoveAsync(rejected.OperationId));
        Assert.AreEqual(retry.OperationId, (await reopened.ReadNamespaceMemberLocationAsync(retry.MemberId)).CompletedMoveOperationId);
        Assert.AreEqual(retry.Target.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(retry.MemberId)).RelativePath);
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveCompletionAsync(rejected.OperationId));
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("unstarted")]
    [DataRow("native-revision")]
    [DataRow("official-revision")]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("descriptor")]
    [DataRow("stale-conflict")]
    [DataRow("stale-observation")]
    public async Task RemoteRejectionRequiresItsOriginalStartIdentityAndFreshProtectedSource(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog, start: invalid != "unstarted", remote: invalid != "ordinary");
        var rejection = RemoteRejection(setup);
        rejection = invalid switch
        {
            "native-revision" => rejection with { NativeRemoteRevision = "another" },
            "official-revision" => rejection with { OfficialRemoteRevision = "another" },
            "binding" => rejection with { Child = rejection.Child with { LocalObject = rejection.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "owner" => rejection with { SourceParent = rejection.SourceParent with { OwnerSid = "S-1-5-21-10-20-30-1002" } },
            "descriptor" => rejection with { TargetParent = rejection.TargetParent with { Dacl = setup.Parent.OriginalDacl } },
            "stale-conflict" => rejection with { ConflictDetectedAt = setup.Move.PreparedAt },
            "stale-observation" => rejection with { Child = rejection.Child with { ObservedAt = setup.Move.PreparedAt } },
            _ => rejection,
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteMoveRejectionAsync(rejection));
        Assert.IsNull(await catalog.ReadNamespaceRemoteMoveRejectionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task RemoteRejectionCannotReplaceAMoveObservation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog);
        await catalog.RecordNamespaceMemberMoveObservationAsync(MoveObservation(setup));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteMoveRejectionAsync(RemoteRejection(setup)));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadNamespaceRemoteMoveRejectionAsync(setup.Move.OperationId));
    }

    [TestMethod]
    public async Task RemoteRejectionReleaseFailureRollsBackItsFactAndEveryReservation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_reject_release BEFORE DELETE ON namespace_member_move_reservations BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
        var rejected = RemoteRejection(setup);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordNamespaceRemoteMoveRejectionAsync(rejected));
        Assert.IsNull(await catalog.ReadNamespaceRemoteMoveRejectionAsync(rejected.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ValidateNamespaceMemberMoveExecutionAsync(rejected.OperationId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_reject_release;");
        Assert.AreEqual(rejected, await catalog.RecordNamespaceRemoteMoveRejectionAsync(rejected));
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("conflict-index")]
    [DataRow("restored-reservation")]
    [DataRow("missing-rejection")]
    public async Task RemoteRejectionReadsRejectCorruptFactsAndReintroducedReservations(string corruption)
    {
        using var fixture = new Fixture(); MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRemoteRejectableMoveAsync(catalog);
            await catalog.RecordNamespaceRemoteMoveRejectionAsync(RemoteRejection(setup));
        }
        string sql = corruption switch
        {
            "hash" => "UPDATE namespace_remote_move_rejections SET fingerprint=zeroblob(32);",
            "conflict-index" => $"UPDATE namespace_remote_move_rejections SET conflict_id='{Guid.NewGuid():D}';",
            "missing-rejection" => "DELETE FROM namespace_remote_move_rejections;",
            _ => $"INSERT INTO namespace_member_move_reservations(operation_id,parent_member_id,child_name_key) VALUES('{setup.Move.OperationId:D}','{setup.Move.Target.ParentMemberId:D}','MOVED.TXT');",
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceRemoteMoveRejectionAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    [TestMethod]
    public async Task MissingLivePredecessorClaimIsNotRebuiltWhenTheCurrentSchemaReopens()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceMemberMoveIntent next;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
            next = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/next.txt");
            await catalog.PrepareNamespaceMemberMoveAsync(next);
        }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_member_move_predecessor_claims;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberMoveAsync(next.OperationId));
    }

    [TestMethod]
    public async Task Schema60MigrationPreservesExistingExclusivePredecessorsWithoutInventingRejection()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceMemberMoveIntent next;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
            next = ContinueMove(setup.Move, completed, setup.Move.Target, "Docs/next.txt");
            await catalog.PrepareNamespaceMemberMoveAsync(next);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_move_rejections; DROP TABLE namespace_member_move_predecessor_claims; CREATE UNIQUE INDEX ux_namespace_move_previous ON namespace_member_moves(previous_move_id) WHERE previous_move_id IS NOT NULL; PRAGMA user_version=60;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(next, await reopened.ValidateNamespaceMemberMoveExecutionAsync(next.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceRemoteMoveRejectionAsync(next.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveStartAsync(next.OperationId));
    }

    private static async Task<MoveFixture> PrepareRemoteRejectableMoveAsync(MirrorPulseProductCatalog catalog,
        bool directory = false, bool start = true, bool remote = true)
    {
        var setup = await PrepareMovableMemberAsync(catalog, directory);
        if (remote) setup = setup with { Move = setup.Move with { Version = 2, RemoteEntry = RemoteMoveReference() } };
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        if (start) await StartMoveAsync(catalog, setup);
        return setup;
    }

    private static MirrorPulseNamespaceRemoteMoveRejection RemoteRejection(MoveFixture setup)
    {
        DateTimeOffset detected = setup.Move.PreparedAt.AddSeconds(2);
        var parent = new MirrorPulseNamespacePermissionVerification(setup.Parent.LocalObject, setup.Parent.OwnerSid,
            setup.ParentProof.TargetDacl, detected.AddSeconds(1));
        return new(1, setup.Move.OperationId, Guid.NewGuid(), detected, detected.AddSeconds(2),
            setup.Move.RemoteEntry?.OriginalRemoteRevision, setup.Move.RemoteEntry?.PreviousRemoteRevision,
            setup.Born.Protection.Verification with { ObservedAt = detected.AddSeconds(1) }, parent, parent);
    }
}
