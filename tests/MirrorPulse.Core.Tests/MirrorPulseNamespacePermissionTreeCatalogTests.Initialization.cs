using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    public async Task InitializationAdmissionSurvivesRestartAndFencesOnlyItsOwnedDispatchRoots()
    {
        using var fixture = new CatalogFixture(); RootId first = RootId.New(), second = RootId.New();
        var roots = new[] { new MirrorPulseNamespaceInitializationRoot(first, Guid.NewGuid()), new(second, Guid.NewGuid()) };
        var intent = new MirrorPulseNamespaceInitializationIntent(1, Guid.NewGuid(), roots, DateTimeOffset.UtcNow);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareNamespaceInitializationAsync(intent);
        roots[0] = new(RootId.New(), Guid.NewGuid());
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await reopened.ReadNamespaceInitializationAsync(intent.OperationId))!;
        CollectionAssert.AreEquivalent(new[] { first, second }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        CollectionAssert.AreEquivalent(new[] { first, second }, (await reopened.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.AreEqual(retained.OperationId, (await reopened.PrepareNamespaceInitializationAsync(retained)).OperationId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceInitializationAsync(retained with { PreparedAt = retained.PreparedAt.AddSeconds(1) }));
    }

    [TestMethod]
    public async Task LaterRootCollisionRollsBackEveryNewInitializationReservation()
    {
        using var fixture = new CatalogFixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        RootId owned = RootId.New(), free = RootId.New();
        var first = Initialization(owned); await catalog.PrepareNamespaceInitializationAsync(first);
        var competing = new MirrorPulseNamespaceInitializationIntent(1, Guid.NewGuid(), [new(free, Guid.NewGuid()), new(owned, Guid.NewGuid())], DateTimeOffset.UtcNow);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceInitializationAsync(competing));
        Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(competing.OperationId));
        CollectionAssert.AreEqual(new[] { owned }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        await catalog.PrepareNamespaceInitializationAsync(Initialization(free));
    }

    [TestMethod]
    [DataRow("missing-root")]
    [DataRow("changed-payload")]
    public async Task DamagedInitializationCannotSilentlyReleaseItsDispatchFence(string damage)
    {
        using var fixture = new CatalogFixture(); var intent = Initialization(RootId.New());
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareNamespaceInitializationAsync(intent);
        await ExecuteSqlAsync(fixture.Paths, damage == "missing-root" ? "DELETE FROM namespace_initialization_roots;" :
            "UPDATE namespace_initialization_intents SET payload=X'00';");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task OriginalAuditCompletionAndFenceReleaseRollbackTogetherThenReplayWithoutReleasingANewerOwner()
    {
        using var fixture = new CatalogFixture();
        var (definition, captures) = Tree(); var intent = Initialization(definition.Anchor.Intent.RootId!.Value) with
        { Roots = [new(definition.Anchor.Intent.RootId.Value, definition.ManifestId)] };
        MirrorPulseNamespaceInitializationCompletion completed;
        MirrorPulseNamespaceMemberProtectionSnapshot[] snapshots;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespaceInitializationAsync(intent);
            snapshots = await RegisterInitializationOriginalsAsync(catalog, definition, captures);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceInitializationAsync(intent.OperationId, snapshots[..^1], AuditTime(definition)));
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_initialization_release BEFORE DELETE ON namespace_initialization_roots BEGIN SELECT RAISE(ABORT,'injected'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceInitializationAsync(intent.OperationId, snapshots, AuditTime(definition)));
            Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
            CollectionAssert.AreEqual(new[] { definition.Anchor.Intent.RootId.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_initialization_release;");
            completed = await catalog.CompleteNamespaceInitializationAsync(intent.OperationId, snapshots, AuditTime(definition));
            Assert.AreEqual(4, completed.MemberCount); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberRecoveryLineageAsync(
                intent.OperationId, snapshots.Single(snapshot => snapshot.Context.Location.RelativePath == definition.Anchor.Baseline.RelativePath).Context.Member.MemberId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await reopened.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        var newer = Initialization(definition.Anchor.Intent.RootId!.Value); await reopened.PrepareNamespaceInitializationAsync(newer);
        Assert.AreEqual(completed, await reopened.CompleteNamespaceInitializationAsync(intent.OperationId, snapshots, AuditTime(definition).AddSeconds(10)));
        CollectionAssert.AreEqual(new[] { definition.Anchor.Intent.RootId.Value }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsNull(await reopened.ReadNamespaceInitializationCompletionAsync(newer.OperationId));
        await ExecuteSqlAsync(fixture.Paths, $"INSERT INTO namespace_initialization_roots(root_id,operation_id,manifest_id) VALUES('{RootId.New().Value:D}','{intent.OperationId:D}','{Guid.NewGuid():D}');");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceInitializationStatusAsync(intent.OperationId));
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_initialization_roots WHERE operation_id='{intent.OperationId:D}';");
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_initialization_completions SET payload=X'00';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task MigrationCreatesNoInitializationOrReadinessForLegacyCatalogs()
    {
        using var fixture = new CatalogFixture(); await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_initialization_roots; DROP TABLE namespace_initialization_completions; DROP TABLE namespace_initialization_intents; PRAGMA user_version=49;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await reopened.ReadNamespaceInitializationAsync(Guid.NewGuid()));
    }

    private static MirrorPulseNamespaceInitializationIntent Initialization(RootId root) =>
        new(1, Guid.NewGuid(), [new(root, Guid.NewGuid())], DateTimeOffset.UtcNow);

    private static DateTimeOffset AuditTime(MirrorPulseNamespacePermissionTreeDefinition definition) => definition.Anchor.Intent.PreparedAt.AddSeconds(10);

    [TestMethod]
    public async Task LegacyInitializationCannotAdoptAnAnchorOnlyCompletionEvenWithAValidPayloadHash()
    {
        using var fixture = new CatalogFixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (definition, captures) = Tree();
        var intent = Initialization(definition.Anchor.Intent.RootId!.Value) with { Roots = [new(definition.Anchor.Intent.RootId.Value, definition.ManifestId)] };
        await catalog.PrepareNamespaceInitializationAsync(intent);
        var members = await RegisterInitializationOriginalsAsync(catalog, definition, captures);
        var completed = await catalog.CompleteNamespaceInitializationAsync(intent.OperationId, members, AuditTime(definition));
        byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(completed with { MemberCount = 1 },
            System.Text.Json.JsonSerializerOptions.Web);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE namespace_initialization_completions SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$hash", System.Security.Cryptography.SHA256.HashData(payload));
        command.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceInitializationsAsync());
    }

    private static async Task<MirrorPulseNamespaceMemberProtectionSnapshot[]> RegisterInitializationOriginalsAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespacePermissionTreeDefinition definition, MirrorPulseNamespacePermissionTreeMember[] captures)
    {
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, captures);
        await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        var anchor = definition.Anchor.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = definition.Anchor.Baseline.LocalObject with { LocalFileId = definition.Anchor.Baseline.LocalObject.SyncRootFileId }
        };
        var anchorIntent = definition.Anchor.Intent with
        {
            EvidenceId = anchor.EvidenceId,
            OperationId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = anchor.LocalObject
        };
        var all = new[] { new MirrorPulseNamespacePermissionPreparation(anchor, anchorIntent) }.Concat(captures.Select(capture => capture.Preparation)).ToArray();
        foreach (var preparation in all)
        {
            await catalog.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
            await catalog.RecordNamespacePermissionApplicationAsync(preparation.Intent.OperationId, preparation.Baseline.LocalObject, preparation.Intent.PreparedAt.AddSeconds(1));
            await catalog.VerifyNamespacePermissionChangeAsync(preparation.Intent.OperationId,
                new(preparation.Baseline.LocalObject, preparation.Baseline.OwnerSid, preparation.Intent.TargetDacl, preparation.Intent.PreparedAt.AddSeconds(2)));
        }
        var members = new List<MirrorPulseNamespaceMemberRegistration>();
        foreach (var preparation in all)
        {
            var baseline = preparation.Baseline;
            Guid? parent = baseline.RootId is null ? null : baseline.RelativePath.Contains('/')
                ? captures.Single(capture => capture.Preparation.Baseline.RelativePath == baseline.RelativePath[..baseline.RelativePath.LastIndexOf('/')]).Preparation.Baseline.EvidenceId
                : anchor.EvidenceId;
            var member = new MirrorPulseNamespaceMemberRegistration(1, baseline.EvidenceId, parent, MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
                baseline.EvidenceId, preparation.Intent.OperationId, baseline.RootId, baseline.RelativePath, baseline.LocalObject,
                baseline.IsDirectory, baseline.OwnerSid, preparation.Intent.PreparedAt.AddSeconds(3));
            await catalog.RegisterNamespaceMemberAsync(member); members.Add(member);
        }
        var result = new List<MirrorPulseNamespaceMemberProtectionSnapshot>();
        foreach (var member in members) result.Add((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!);
        return result.ToArray();
    }
}
