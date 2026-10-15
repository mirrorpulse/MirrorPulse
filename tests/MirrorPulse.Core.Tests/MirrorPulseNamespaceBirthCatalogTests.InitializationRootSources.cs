using System.Text;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CurrentInitializationRetainsMixedRootProvenanceAndCompletesBornRootsWithoutOriginalCaptures()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceInitializationIntent intent; MirrorPulseNamespaceInitializationCompletion completion;
        MirrorPulseNamespaceCurrentTreeSnapshot inventory; MirrorPulseNamespaceBirthObservation first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareBornRootInitializationAsync(catalog);
            intent = setup.Intent; first = (await catalog.ReadNamespaceBirthObservationAsync(setup.Roots[0].OriginId))!;
            await catalog.PrepareNamespaceInitializationAsync(intent);
            Assert.HasCount(3, await catalog.ReadNamespaceDispatchFencedRootsAsync());
            inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor);
            Assert.HasCount(7, inventory.Members);
            completion = await catalog.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, inventory, intent.PreparedAt.AddMinutes(1));
            Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, completion.InventoryKind);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            foreach (var bornRoot in setup.Roots)
            {
                Assert.IsEmpty(await catalog.ReadNamespacePermissionTreesAsync(bornRoot.RootId!.Value));
                Assert.AreEqual(bornRoot, await catalog.ReadNamespaceMemberAsync(bornRoot.MemberId));
            }
            Assert.AreEqual(first, await catalog.ReadNamespaceBirthObservationAsync(setup.Roots[0].OriginId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceInitializationAsync(
                intent.OperationId, inventory.Members, completion.CompletedAt));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.PrepareNamespaceInitializationAsync(intent);
        CollectionAssert.AreEqual(intent.Roots.ToArray(), retained.Roots.ToArray());
        Assert.AreEqual(completion, await reopened.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, inventory, completion.CompletedAt.AddMinutes(1)));
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
    }

    [TestMethod]
    [DataRow("version-one")]
    [DataRow("original-route")]
    [DataRow("both-sources")]
    [DataRow("no-source")]
    [DataRow("duplicate-birth")]
    public async Task BornRootAdmissionRejectsAmbiguousOrLegacySourceShapesBeforeReservingRoots(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog);
        var root = setup.Intent.Roots[1];
        var intent = damage switch
        {
            "version-one" => setup.Intent with { Version = 1 },
            "original-route" => setup.Intent with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory },
            "both-sources" => setup.Intent with { Roots = [root with { ManifestId = Guid.NewGuid() }] },
            "no-source" => setup.Intent with { Roots = [root with { BornRootMemberId = null }] },
            _ => setup.Intent with { Roots = [root, root with { RootId = RootId.New() }] }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceInitializationAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(intent.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("missing-member")]
    [DataRow("wrong-root")]
    [DataRow("ordinary-child")]
    [DataRow("captured-root")]
    [DataRow("before-registration")]
    public async Task BornRootAdmissionRequiresItsActualRegisteredTopLevelProvenance(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog);
        var root = setup.Intent.Roots[1];
        var child = (await catalog.ReadNamespaceMemberChildAsync(setup.Roots[0].MemberId, "child.bin"))!;
        var intent = damage switch
        {
            "missing-member" => setup.Intent with { Roots = [root with { BornRootMemberId = Guid.NewGuid() }] },
            "wrong-root" => setup.Intent with { Roots = [root with { RootId = RootId.New() }] },
            "ordinary-child" => setup.Intent with { Roots = [root with { BornRootMemberId = child.MemberId }] },
            "captured-root" => setup.Intent with { Roots = [root with { BornRootMemberId = setup.OriginalRoot.MemberId }] },
            _ => setup.Intent with { Roots = [root], PreparedAt = setup.Roots[0].RegisteredAt.AddTicks(-1) }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceInitializationAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(intent.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task PendingBornRootIndexesRemainExactAcrossRestartAndCannotSubstituteAnotherMember()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceInitializationIntent intent;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareBornRootInitializationAsync(catalog); intent = setup.Intent;
            await catalog.PrepareNamespaceInitializationAsync(intent);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceInitializationAsync(
                intent with { Roots = [intent.Roots[0], intent.Roots[1] with { BornRootMemberId = Guid.NewGuid() }, intent.Roots[2]] }));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var pending = (await reopened.ReadPendingNamespaceInitializationsAsync()).Single();
            CollectionAssert.AreEqual(intent.Roots.ToArray(), pending.Intent.Roots.ToArray());
        }
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_initialization_roots SET born_member_id='{Guid.NewGuid():D}' WHERE root_id='{intent.Roots[1].RootId.Value:D}';");
        await using var damaged = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => damaged.ReadPendingNamespaceInitializationsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => damaged.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema53RootReservationMigrationPreservesLegacyPayloadFingerprintAndOwnership()
    {
        using var fixture = new Fixture();
        var intent = new MirrorPulseNamespaceInitializationIntent(1, Guid.NewGuid(), [new(RootId.New(), Guid.NewGuid())], DateTimeOffset.UtcNow);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) await catalog.PrepareNamespaceInitializationAsync(intent);
        string before = await ReadInitializationRootStorageAsync(fixture);
        await ExecuteSqlAsync(fixture.Paths, """
            BEGIN;
            CREATE TABLE namespace_initialization_roots_legacy (
                root_id TEXT PRIMARY KEY, operation_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
                manifest_id TEXT NOT NULL UNIQUE);
            INSERT INTO namespace_initialization_roots_legacy SELECT root_id,operation_id,manifest_id FROM namespace_initialization_roots;
            DROP TABLE namespace_initialization_roots;
            ALTER TABLE namespace_initialization_roots_legacy RENAME TO namespace_initialization_roots;
            CREATE INDEX namespace_initialization_owner ON namespace_initialization_roots(operation_id);
            PRAGMA user_version=53;
            COMMIT;
            """);
        await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var retained = (await migrated.ReadPendingNamespaceInitializationsAsync()).Single().Intent;
            CollectionAssert.AreEqual(intent.Roots.ToArray(), retained.Roots.ToArray());
            Assert.IsNull(retained.Roots[0].BornRootMemberId);
            CollectionAssert.AreEqual(new[] { intent.Roots[0].RootId }, (await migrated.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        Assert.AreEqual(before, await ReadInitializationRootStorageAsync(fixture));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_check;";
        Assert.AreEqual(0L, (long)(await query.ExecuteScalarAsync())!);
    }

    private static async Task<string> ReadInitializationRootStorageAsync(Fixture fixture)
    {
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_initialization_intents;";
        string value = (string)(await query.ExecuteScalarAsync())!;
        Assert.DoesNotContain(Encoding.UTF8.GetString(Convert.FromHexString(value.Split(':')[0])), "BornRootMemberId");
        query.CommandText = "SELECT root_id||':'||operation_id||':'||manifest_id FROM namespace_initialization_roots ORDER BY root_id;";
        return value + "\n" + (string)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<BornRootInitializationSetup> PrepareBornRootInitializationAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareCurrentMovableMemberAsync(catalog);
        Guid anchorId = setup.ParentMember.ParentMemberId!.Value;
        var anchor = (await catalog.ReadNamespaceMemberAsync(anchorId))!;
        var permission = (await catalog.ReadLatestNamespacePermissionChangeAsync(anchor.OriginId))!;
        var roots = new List<MirrorPulseNamespaceMemberRegistration>();
        for (int index = 0; index < 2; index++)
        {
            var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), RootId.New(), anchor.OriginId,
                permission.Intent.OperationId, anchor.LocalObject, "Born" + index, true,
                MirrorPulseNamespaceBirthOrigin.RemotePopulation, Role, DateTimeOffset.UtcNow.AddMinutes(1));
            var born = await AddBornAsync(catalog, birth);
            var root = await catalog.RegisterNamespaceMemberAsync(BornMember(born, anchor)); roots.Add(root);
            var child = await AddBornAsync(catalog, NestedBirth(born, "child.bin", false));
            await catalog.RegisterNamespaceMemberAsync(BornMember(child, root));
        }
        var tree = (await catalog.ReadNamespacePermissionTreesAsync(setup.Move.RootId)).Single();
        var references = new[] { new MirrorPulseNamespaceInitializationRoot(setup.Move.RootId, tree.Definition.ManifestId) }
            .Concat(roots.Select(root => new MirrorPulseNamespaceInitializationRoot(root.RootId!.Value, Guid.Empty) { BornRootMemberId = root.MemberId })).ToArray();
        var intent = new MirrorPulseNamespaceInitializationIntent(2, Guid.NewGuid(), references, DateTimeOffset.UtcNow.AddMinutes(5))
        { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        return new(intent, anchorId, setup.ParentMember, roots.AsReadOnly());
    }

    private sealed record BornRootInitializationSetup(MirrorPulseNamespaceInitializationIntent Intent, Guid Anchor,
        MirrorPulseNamespaceMemberRegistration OriginalRoot, IReadOnlyList<MirrorPulseNamespaceMemberRegistration> Roots);
}
