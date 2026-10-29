using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task BornCancellationAdmitsOneOriginalPurposeAcrossCatalogOwnersWithoutChangingFirstFacts(bool enabled, bool nested)
    {
        // Actual private SQLite transactions; all permission observations are synthetic.
        // This proves admission ownership and persistence, not native permission or readiness.
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseNamespaceInitializationIntent admitted; MirrorPulseRootLabelCancellationRecoveryOwner owner;
        var first = new Dictionary<Guid, string>();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareCancellationInitializationFixtureAsync(catalog, enabled, nested);
            foreach (var member in setup.Members)
                first[member.MemberId] = JsonSerializer.Serialize(await catalog.ReadNamespaceBirthObservationAsync(member.OriginId));
            Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
            _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Rename.Proof!.CapturedAt.AddSeconds(1));
            var rotatedTree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            admitted = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, Guid.NewGuid(),
                rotatedTree.Anchor.Context.Protection.VerifiedAt.AddSeconds(1)))!;
            Assert.IsNotNull(admitted); Assert.AreEqual(5, admitted.Version);
            Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, admitted.RequestedInventoryKind);
            Assert.HasCount(1, admitted.Roots); Assert.AreEqual(setup.Root.MemberId, admitted.Roots[0].BornRootMemberId);
            Assert.AreEqual(setup.Move.RootId, admitted.Roots[0].RootId); Assert.AreEqual(Guid.Empty, admitted.Roots[0].ManifestId);
            Assert.IsNotNull(admitted.RootLabelCancellation);
            Assert.AreEqual(setup.Move.OperationId, admitted.RootLabelCancellation.RootLabelOperationId);
            Assert.AreEqual(setup.Root.MemberId, admitted.RootLabelCancellation.RootMemberId);
            Assert.AreEqual(setup.Anchor.MemberId, admitted.RootLabelCancellation.AnchorMemberId);
            owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId))!;
            Assert.IsNotNull(owner); Assert.AreEqual(admitted.OperationId, owner.InitializationOperationId);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retryId = Guid.NewGuid();
        var retry = await reopened.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, retryId, admitted.PreparedAt.AddMinutes(1));
        Assert.AreEqual(JsonSerializer.Serialize(admitted), JsonSerializer.Serialize(retry));
        Assert.IsNull(await reopened.ReadNamespaceInitializationAsync(retryId));
        Assert.AreEqual(owner, await reopened.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        var plan = await reopened.ReadRootLabelPendingPermissionRecoveryAsync(setup.Move.OperationId);
        Assert.IsNotNull(plan); Assert.AreEqual(owner, plan.Owner); Assert.IsEmpty(plan.PendingOriginals);
        CollectionAssert.AreEqual(setup.Members.Select(member => member.MemberId).ToArray(), plan.BornMembers.ToArray());
        foreach (var member in setup.Members)
        {
            Assert.AreEqual(member, await reopened.ReadNamespaceMemberAsync(member.MemberId));
            Assert.AreEqual(first[member.MemberId], JsonSerializer.Serialize(await reopened.ReadNamespaceBirthObservationAsync(member.OriginId)));
        }
        Assert.IsNull((await reopened.ReadNamespaceInitializationStatusAsync(admitted.OperationId)).Completion);
        Assert.HasCount(1, await reopened.ReadPendingNamespaceInitializationsAsync());
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationAdmissionPreservesAnExistingLegacyInitializerWithoutReplacingItsFingerprint(bool enabled)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
        var before = await catalog.ReadNamespaceInitializationStatusAsync(initializer);
        var owner = await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId);
        var result = await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, Guid.NewGuid(), setup.Move.PreparedAt.AddMinutes(1));
        Assert.AreEqual(JsonSerializer.Serialize(before.Intent), JsonSerializer.Serialize(result));
        Assert.IsNull(result!.RootLabelCancellation); Assert.AreEqual(owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationStatusAsync(initializer)));
        Assert.HasCount(1, await catalog.ReadPendingNamespaceInitializationsAsync());
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CapturedRootCancellationWithoutAnInitializerKeepsItsOriginalRoute(bool enabled)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        Assert.IsNull(await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, Guid.NewGuid(), setup.Move.PreparedAt.AddMinutes(1)));
        Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync());
        Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow("generic-route")]
    [DataRow("before-original")]
    [DataRow("occupied-identity")]
    [DataRow("sealed-move")]
    public async Task DedicatedCancellationAdmissionCannotBorrowAGenericRouteEarlierTimeAnotherIdentityOrSealedMove(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = damage == "sealed-move" ? await PrepareBornRootJournalFixtureAsync(catalog, true, false)
            : await PrepareCancellationInitializationFixtureAsync(catalog, true, false);
        Guid candidate = Guid.NewGuid(); DateTimeOffset time = setup.Move.PreparedAt.AddMinutes(1);
        if (damage == "generic-route")
        {
            var admitted = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, candidate, time))!;
            Guid substitute = Guid.NewGuid();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceInitializationAsync(admitted with { OperationId = substitute }));
            Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(substitute)); return;
        }
        if (damage == "before-original") time = setup.Rename.Proof!.CapturedAt.AddSeconds(-1);
        if (damage == "occupied-identity")
            await catalog.PrepareNamespaceInitializationAsync(new(3, candidate, [], time)
            { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory });
        if (damage == "sealed-move") await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, candidate, time));
        if (damage == "sealed-move") Assert.IsNotNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        else Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.HasCount(damage == "occupied-identity" ? 1 : 0, await catalog.ReadPendingNamespaceInitializationsAsync());
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DedicatedInitializerCompletesOnlyAfterItsExactAtomicCancellationAndKeepsHistoricalReplay(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseNamespaceInitializationIntent intent;
        MirrorPulseRootLabelUnsealedCancellation cancellation; MirrorPulseNamespaceInitializationCompletion completed;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareCancellationInitializationFixtureAsync(catalog, enabled, true);
            intent = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, Guid.NewGuid(), setup.Move.PreparedAt.AddMinutes(1)))!;
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, inventory, intent.PreparedAt.AddSeconds(1)));
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            var observation = RootCancellationFromTree(setup, tree) with
            {
                PublicNotMovedAt = intent.PreparedAt.AddSeconds(2),
                VerifiedAt = intent.PreparedAt.AddSeconds(4),
                Anchor = tree.Anchor.Context.Verification with { ObservedAt = intent.PreparedAt.AddSeconds(3) },
                Members = tree.Members.Select(member => new MirrorPulseRootLabelCancellationMember(member.Context.Member.MemberId,
                    member.Context.Protection.ProtectionId, member.Context.Verification with { ObservedAt = intent.PreparedAt.AddSeconds(3) })).ToArray()
            };
            cancellation = new(2, setup.Root.MemberId, observation,
                new(1, setup.Move.OperationId, observation.PublicEvidenceSha256, tree.Members.Select(PartialRecoveryCancellationEntry).ToArray()))
            { RecoveryOwner = tree.RecoveryOwner };
            await catalog.CancelUnsealedRootLabelAsync(cancellation);
            Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
            inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
            completed = await catalog.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, inventory, intent.PreparedAt.AddSeconds(5));
            Assert.AreEqual(setup.Move.RootId, completed.ScopeRoot); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, (await reopened.ReadNamespaceInitializationStatusAsync(intent.OperationId)).Completion);
        Assert.AreEqual(JsonSerializer.Serialize(cancellation), JsonSerializer.Serialize(await reopened.CancelUnsealedRootLabelAsync(cancellation)));
        Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync()); Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationInitialization")]
    [DataRow("label")]
    [DataRow("anchor")]
    [DataRow("proof")]
    public async Task AnInternallyRehashedInitializerCannotSubstituteItsOriginalCancellationPurpose(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCancellationInitializationFixtureAsync(catalog, true, false);
        var intent = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId, Guid.NewGuid(), setup.Move.PreparedAt.AddMinutes(1)))!;
        var purpose = intent.RootLabelCancellation!;
        var changed = intent with
        {
            RootLabelCancellation = damage switch
            {
                "label" => purpose with { RootLabelOperationId = Guid.NewGuid() },
                "anchor" => purpose with { AnchorMemberId = Guid.NewGuid() },
                _ => purpose with { LabelProofSha256 = new string('A', 64) }
            }
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed, BornOwnerCatalogOptions);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await raw.OpenAsync(); await using var update = raw.CreateCommand();
            update.CommandText = "UPDATE namespace_initialization_intents SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    private static Task<RootSubtreeFixture> PrepareCancellationInitializationFixtureAsync(MirrorPulseProductCatalog catalog, bool enabled, bool nested) =>
        PrepareBornRootJournalFixtureAsync(catalog, enabled, nested, async (store, members) =>
        {
            DateTimeOffset time = members.Max(member => member.RegisteredAt).AddMilliseconds(20);
            foreach (var member in members)
            {
                await store.RecordNamespaceBirthProtectionAsync(await LocatedBornEpochAsync(store, member, time));
                time = time.AddMilliseconds(20);
            }
        });
}
