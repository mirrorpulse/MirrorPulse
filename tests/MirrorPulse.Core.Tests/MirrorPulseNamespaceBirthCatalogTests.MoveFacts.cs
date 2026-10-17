using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeAndSingleItemProjectionFactsSurviveOwnersWithoutSettlingNamesOrRootFence(bool directory)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveObservation observation;
        MirrorPulseNamespaceMemberMoveProjection projection;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory);
            await StartMoveAsync(first, setup);
            observation = MoveObservation(setup); projection = MoveProjection(observation);
            Assert.IsNull(await first.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
            Assert.AreEqual(observation, await first.RecordNamespaceMemberMoveObservationAsync(observation));
            Assert.IsNull(await first.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
            Assert.AreEqual(projection, await first.RecordNamespaceMemberMoveProjectionAsync(projection));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(observation, await second.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        Assert.AreEqual(projection, await second.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        Assert.AreEqual(observation, await second.RecordNamespaceMemberMoveObservationAsync(observation));
        Assert.AreEqual(projection, await second.RecordNamespaceMemberMoveProjectionAsync(projection));
        Assert.AreEqual(setup.Move, await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        Assert.AreEqual(setup.Born.Observation, await second.ReadNamespaceBirthObservationAsync(setup.Born.Birth.OperationId));
        Assert.AreEqual(setup.Parent, await second.ReadNamespacePermissionBaselineAsync(setup.Parent.EvidenceId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("child-binding")]
    [DataRow("parent-binding")]
    [DataRow("child-owner")]
    [DataRow("parent-owner")]
    [DataRow("child-dacl")]
    [DataRow("parent-dacl")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("path")]
    [DataRow("kind")]
    [DataRow("child-time")]
    [DataRow("parent-time")]
    public async Task NativeMoveFactsRejectSubstitutionsAndPrematureObservations(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var fact = MoveObservation(setup);
        var other = mismatch switch
        {
            "child-binding" => fact with { Child = fact.Child with { LocalObject = fact.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent-binding" => fact with { Parent = fact.Parent with { LocalObject = fact.Parent.LocalObject with { VolumeSerialNumber = 99 } } },
            "child-owner" => fact with { Child = fact.Child with { OwnerSid = "S-1-5-21-100-200-300-1002" } },
            "parent-owner" => fact with { Parent = fact.Parent with { OwnerSid = "S-1-5-21-100-200-300-1002" } },
            "child-dacl" => fact with { Child = fact.Child with { Dacl = setup.Parent.OriginalDacl } },
            "parent-dacl" => fact with { Parent = fact.Parent with { Dacl = setup.Parent.OriginalDacl } },
            "item" => fact with { ItemId = Guid.NewGuid() },
            "remote" => fact with { RemoteId = "foreign" },
            "path" => fact with { RelativePath = setup.Move.Source.RelativePath },
            "kind" => fact with { IsDirectory = true },
            "child-time" => fact with { Child = fact.Child with { ObservedAt = setup.Move.PreparedAt } },
            _ => fact with { Parent = fact.Parent with { ObservedAt = setup.Move.PreparedAt } }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(other));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(fact, await catalog.RecordNamespaceMemberMoveObservationAsync(fact));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("path")]
    [DataRow("time")]
    public async Task ProjectionRequiresTheRetainedNativeObjectWithoutEquatingItsRevisionToSourceAcceptance(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        var projection = MoveProjection(native);
        var other = mismatch switch
        {
            "item" => projection with { ItemId = Guid.NewGuid() },
            "remote" => projection with { RemoteId = "foreign" },
            "path" => projection with { RelativePath = setup.Move.Source.RelativePath },
            _ => projection with { ObservedAt = native.Parent.ObservedAt.AddTicks(-1) }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveProjectionAsync(other));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        Assert.AreEqual(projection, await catalog.RecordNamespaceMemberMoveProjectionAsync(projection));
        Assert.AreNotEqual(native.NativeRemoteRevision, projection.ProjectedRemoteRevision);
        Assert.AreEqual(setup.Born.Observation.RemoteRevision, (await catalog.ReadNamespaceBirthObservationAsync(setup.Born.Birth.OperationId))!.RemoteRevision);
    }

    [TestMethod]
    public async Task MissingStartOrNativeFactAndCancelledRecordingCannotInventMoveEvidence()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var native = MoveObservation(setup);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(native));
        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceMemberMoveProjectionAsync(MoveProjection(native)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(native, cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
    }

    [TestMethod]
    public async Task HistoricalMoveFactsSurviveProtectionRotationWithoutRefreshingCurrentExecution()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); await StartMoveAsync(catalog, setup);
        var native = MoveObservation(setup); var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native); await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        await catalog.RecordNamespaceBirthProtectionAsync(setup.Born.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = setup.Born.Protection.ProtectionId,
            Verification = setup.Born.Protection.Verification with { ObservedAt = projection.ObservedAt.AddSeconds(1) }
        });
        Assert.AreEqual(native, await catalog.RecordNamespaceMemberMoveObservationAsync(native));
        Assert.AreEqual(projection, await catalog.RecordNamespaceMemberMoveProjectionAsync(projection));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveObservationAsync(native with { NativeRemoteRevision = "replacement" }));
    }

    [TestMethod]
    public async Task Schema36MigrationRetainsPendingIntentAndStartWithoutInventingNativeOrProjectionFacts()
    {
        using var fixture = new Fixture(); MoveFixture setup;
        var started = DateTimeOffset.UtcNow;
        await using (var previous = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { setup = await PrepareMovableMemberAsync(previous); await StartMoveAsync(previous, setup); started = setup.Move.PreparedAt.AddSeconds(1); }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_member_move_projections; DROP TABLE namespace_member_move_observations; PRAGMA user_version=36;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Move, await current.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(new(1, setup.Move.OperationId, started), await current.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsNull(await current.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
        Assert.IsNull(await current.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Member, await current.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA foreign_key_check;"; Assert.IsNull(await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(67L, await query.ExecuteScalarAsync());
    }

    private static async Task StartMoveAsync(MirrorPulseProductCatalog catalog, MoveFixture setup)
    {
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
    }
    private static MirrorPulseNamespaceMemberMoveObservation MoveObservation(MoveFixture setup) => new(1, setup.Move.OperationId,
        setup.Move.ItemId, setup.Move.RemoteId, "native-revision", setup.Move.Target.RelativePath, setup.Member.IsDirectory, true, 1,
        setup.Born.Protection.Verification with { ObservedAt = setup.Move.PreparedAt.AddSeconds(2) },
        new(setup.Parent.LocalObject, setup.Parent.OwnerSid, setup.ParentProof.TargetDacl, setup.Move.PreparedAt.AddSeconds(3)));
    private static MirrorPulseNamespaceMemberMoveProjection MoveProjection(MirrorPulseNamespaceMemberMoveObservation observation) => new(1,
        observation.OperationId, observation.ItemId, observation.RemoteId, "official-baseline", observation.RelativePath, observation.Parent.ObservedAt.AddSeconds(1));
}
