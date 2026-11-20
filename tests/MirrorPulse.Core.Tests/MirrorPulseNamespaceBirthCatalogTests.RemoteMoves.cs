using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteMoveAdmissionRetainsTheOriginalBatchAndBindingAcrossOwners(bool directory)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog, directory);
            intent = setup.Move with { Version = 2, RemoteEntry = RemoteMoveReference() };
            Assert.AreEqual(intent, await catalog.PrepareNamespaceMemberMoveAsync(intent));
            Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
            Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(intent, await reopened.ReadNamespaceMemberMoveAsync(intent.OperationId));
        var owner = intent.RemoteEntry!;
        Assert.AreEqual(intent, await reopened.ReadNamespaceRemoteMoveAsync(owner.InstanceId, owner.BatchId, owner.Ordinal));
        Assert.IsNull(await reopened.ReadNamespaceRemoteMoveAsync(owner.InstanceId, owner.BatchId, 0));
        Assert.IsNull(await reopened.ReadNamespaceRemoteMoveAsync(owner.InstanceId, $"{owner.InstanceId}/another", owner.Ordinal));
        Assert.IsTrue(await reopened.CanResumeNamespaceRemoteMoveAsync(intent));
        Assert.IsFalse(await reopened.CanResumeNamespaceRemoteMoveAsync(intent with { OperationId = Guid.NewGuid() }));
        Assert.AreEqual(intent, await reopened.PrepareNamespaceMemberMoveAsync(intent));
        Assert.AreEqual(intent, await reopened.ValidateNamespaceMemberMoveExecutionAsync(intent.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveStartAsync(intent.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveObservationAsync(intent.OperationId));
        Assert.AreEqual(setup.Member.LocalObject, intent.LocalObject);
        Assert.AreEqual(setup.Move.Source, intent.Source); Assert.AreEqual(setup.Move.Target, intent.Target);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("batch")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("ordinal")]
    [DataRow("change")]
    [DataRow("previous-revision")]
    [DataRow("revision")]
    [DataRow("original-revision")]
    [DataRow("downgrade")]
    public async Task RemoteMoveReplayCannotReplaceItsOriginalBatchOrRevisionEvidence(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var remote = RemoteMoveReference(); var intent = setup.Move with { Version = 2, RemoteEntry = remote };
        await catalog.PrepareNamespaceMemberMoveAsync(intent);
        var altered = change switch
        {
            "batch" => intent with { RemoteEntry = remote with { BatchId = $"{remote.InstanceId}/another" } },
            "fingerprint" => intent with { RemoteEntry = remote with { BatchFingerprint = new string('B', 64) } },
            "count" => intent with { RemoteEntry = remote with { BatchEntryCount = 4 } },
            "ordinal" => intent with { RemoteEntry = remote with { Ordinal = 0 } },
            "change" => intent with { RemoteEntry = remote with { ChangeId = "another" } },
            "previous-revision" => intent with { RemoteEntry = remote with { PreviousRemoteRevision = "another" } },
            "revision" => intent with { RemoteEntry = remote with { RemoteRevision = "another" } },
            "original-revision" => intent with { RemoteEntry = remote with { OriginalRemoteRevision = "another" } },
            _ => intent with { Version = 1, RemoteEntry = null },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.AreEqual(intent, await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("legacy-envelope")]
    [DataRow("missing-envelope")]
    [DataRow("reference-version")]
    [DataRow("instance")]
    [DataRow("batch-scope")]
    [DataRow("empty-batch")]
    [DataRow("fingerprint")]
    [DataRow("lowercase-fingerprint")]
    [DataRow("ordinal")]
    [DataRow("count")]
    [DataRow("change")]
    [DataRow("revision")]
    public async Task RemoteMoveRejectsIncompleteOrNoncanonicalReferencesBeforeReservingNames(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var remote = RemoteMoveReference();
        var intent = setup.Move with { Version = 2, RemoteEntry = remote };
        var altered = invalid switch
        {
            "version" => intent with { Version = 3 },
            "legacy-envelope" => intent with { Version = 1 },
            "missing-envelope" => intent with { RemoteEntry = null },
            "reference-version" => intent with { RemoteEntry = remote with { Version = 2 } },
            "instance" => intent with { RemoteEntry = remote with { InstanceId = default } },
            "batch-scope" => intent with { RemoteEntry = remote with { BatchId = "another/batch" } },
            "empty-batch" => intent with { RemoteEntry = remote with { BatchId = $"{remote.InstanceId}/" } },
            "fingerprint" => intent with { RemoteEntry = remote with { BatchFingerprint = "invalid" } },
            "lowercase-fingerprint" => intent with { RemoteEntry = remote with { BatchFingerprint = new string('a', 64) } },
            "ordinal" => intent with { RemoteEntry = remote with { Ordinal = remote.BatchEntryCount } },
            "count" => intent with { RemoteEntry = remote with { BatchEntryCount = 0 } },
            "change" => intent with { RemoteEntry = remote with { ChangeId = "" } },
            _ => intent with { RemoteEntry = remote with { RemoteRevision = "invalid\0revision" } },
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    public async Task RemoteMoveCompletesUsingTheExistingOriginalBindingAndProjectionReceipts()
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            intent = setup.Move with { Version = 2, RemoteEntry = RemoteMoveReference() };
            setup = setup with { Move = intent };
            await StartMoveAsync(catalog, setup);
            var native = MoveObservation(setup); await catalog.RecordNamespaceMemberMoveObservationAsync(native);
            var projection = MoveProjection(native);
            await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
            await catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(intent, await reopened.ReadNamespaceMemberMoveAsync(intent.OperationId));
        Assert.IsNotNull(await reopened.ReadNamespaceMemberMoveCompletionAsync(intent.OperationId));
        Assert.AreEqual(intent, await reopened.ReadNamespaceRemoteMoveAsync(intent.RemoteEntry!.InstanceId,
            intent.RemoteEntry.BatchId, intent.RemoteEntry.Ordinal));
        Assert.IsFalse(await reopened.CanResumeNamespaceRemoteMoveAsync(intent));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        Assert.IsNull(await reopened.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(intent.Target.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(intent.MemberId)).RelativePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteMoveRejectsReceiptsForAnUnobservedIdentityOrIncomingRevision(bool projectionMismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        setup = setup with { Move = setup.Move with { Version = 2, RemoteEntry = RemoteMoveReference() } };
        await StartMoveAsync(catalog, setup); var native = MoveObservation(setup);
        if (projectionMismatch)
        {
            await catalog.RecordNamespaceMemberMoveObservationAsync(native);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveProjectionAsync(
                MoveProjection(native) with { ProjectedRemoteRevision = "unobserved" }));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(
                native with { NativeRemoteRevision = "unobserved" }));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        }
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    [DataRow("lost-index")]
    [DataRow("batch-index")]
    [DataRow("ordinal-index")]
    [DataRow("hash-index")]
    [DataRow("change-index")]
    public async Task RemoteMoveReadsRejectMissingOrChangedBatchOwnership(string corruption)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceMemberMoveIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog); intent = setup.Move with { Version = 2, RemoteEntry = RemoteMoveReference() };
            await catalog.PrepareNamespaceMemberMoveAsync(intent);
        }
        string sql = corruption switch
        {
            "lost-index" => "DELETE FROM namespace_remote_move_entries;",
            "batch-index" => "UPDATE namespace_remote_move_entries SET batch_id='another';",
            "ordinal-index" => "UPDATE namespace_remote_move_entries SET ordinal=0;",
            "hash-index" => "UPDATE namespace_remote_move_entries SET batch_fingerprint='" + new string('B', 64) + "';",
            _ => "UPDATE namespace_remote_move_entries SET change_id='another';",
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberMoveAsync(intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ValidateNamespaceMemberMoveExecutionAsync(intent.OperationId));
    }

    [TestMethod]
    public async Task Schema58UpgradePreservesOrdinaryMoveWithoutInventingRemoteOwnership()
    {
        using var fixture = new Fixture(); MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog); await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_move_entries; PRAGMA user_version=58;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Move, await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsNull((await reopened.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId)).RemoteEntry);
    }

    [TestMethod]
    [DataRow("ordinal")]
    [DataRow("change")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("next-entry")]
    public async Task ACompletedRemoteEntryCannotBeReassignedToAnotherMove(string candidate)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var remote = RemoteMoveReference();
        setup = setup with { Move = setup.Move with { Version = 2, RemoteEntry = remote } };
        await StartMoveAsync(catalog, setup); var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native); await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        var completion = await catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection));
        var nextReference = candidate switch
        {
            "ordinal" => remote with { ChangeId = "different" },
            "change" => remote with { Ordinal = 2 },
            "fingerprint" => remote with { Ordinal = 2, ChangeId = "different", BatchFingerprint = new string('B', 64) },
            "count" => remote with { Ordinal = 2, ChangeId = "different", BatchEntryCount = 4 },
            _ => remote with { Ordinal = 2, ChangeId = "different" },
        };
        var next = ContinueMove(setup.Move, completion, setup.Move.Source, "Docs/again.txt") with { RemoteEntry = nextReference };
        if (candidate == "next-entry")
            Assert.AreEqual(next, await catalog.PrepareNamespaceMemberMoveAsync(next));
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(next));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(next.OperationId));
            Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(next.RootId));
        }
        Assert.AreEqual(setup.Move, await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    private static MirrorPulseNamespaceRemoteMoveReference RemoteMoveReference()
    {
        var instance = InstanceId.New();
        return new(1, instance, $"{instance}/batch", new string('A', 64), 3, 1, "change", "base", "official-baseline", "native-revision");
    }
}
