using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelPartialRecovery")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LegacyUnsealedCancellationPreservesItsPendingInitializerAndMixedVerifiedProtectionEpochs(bool enabled)
    {
        // Actual SQLite history and synthetic permission facts only; no native or SDK result is asserted.
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        var initializer = Guid.NewGuid(); Guid rootEpoch; MirrorPulseRootLabelUnsealedCancellation cancellation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            await catalog.PrepareNamespaceInitializationAsync(new(4, initializer,
                [new(setup.Move.RootId, Guid.NewGuid())], setup.Move.PreparedAt.AddSeconds(-2))
            { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory });
            _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Move.PreparedAt.AddSeconds(-1));
            rootEpoch = await RotateCancellationAdmissionMemberAsync(catalog, setup.Root, setup.Move.PreparedAt.AddSeconds(-1));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var before = await catalog.ReadNamespaceInitializationStatusAsync(initializer);
            Assert.IsNull(before.Completion);
            var lineage = await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializer, setup.Members[1].MemberId);
            Assert.AreNotEqual(lineage[0].BirthProtection!.RoleSid, lineage[1].Permission!.Intent.RoleSid);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Members[1].MemberId));
            var owner = await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId);
            Assert.IsNotNull(owner); Assert.AreEqual(initializer, owner.InitializationOperationId);
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            Assert.AreEqual(owner, tree.RecoveryOwner);
            Assert.AreEqual(rootEpoch, tree.Members.Single(snapshot => snapshot.Context.Member.MemberId == setup.Root.MemberId).Context.Protection.ProtectionId);
            Assert.HasCount(3, tree.Members);
            var entries = tree.Members.Select(PartialRecoveryCancellationEntry).ToArray();
            var observation = RootCancellationFromTree(setup, tree);
            cancellation = new(2, setup.Root.MemberId, observation, new(1, setup.Move.OperationId, observation.PublicEvidenceSha256, entries))
            { RecoveryOwner = owner };
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
            Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationStatusAsync(initializer)));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
            Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        _ = await reopened.CancelUnsealedRootLabelAsync(cancellation);
        Assert.IsNull((await reopened.ReadNamespaceInitializationStatusAsync(initializer)).Completion);
        Assert.AreEqual(initializer, (await reopened.ReadPendingNamespaceInitializationsAsync()).Single().Intent.OperationId);
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, (await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent.Phase);
        foreach (var member in setup.Members) Assert.AreEqual(member, await reopened.ReadNamespaceMemberAsync(member.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ReadNamespaceMemberProtectionSnapshotAsync(setup.Members[1].MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelPartialRecovery")]
    [DataRow("unclassified")]
    [DataRow("foreign-root")]
    [DataRow("missing-reservation")]
    public async Task MixedRoleCancellationCannotBorrowAnUnclassifiedForeignOrDamagedInitializer(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog); Guid initializer = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(new(damage == "unclassified" ? 1 : 4, initializer,
            [new(damage == "foreign-root" ? RootId.New() : setup.Move.RootId, Guid.NewGuid())], setup.Move.PreparedAt.AddSeconds(-2))
        { RequestedInventoryKind = damage == "unclassified" ? null : MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory });
        _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Move.PreparedAt.AddSeconds(-1));
        _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Root, setup.Move.PreparedAt.AddSeconds(-1));
        if (damage == "missing-reservation")
            await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_initialization_roots WHERE operation_id='{initializer:D}';");
        await RequireCancellationAdmissionRefusalAsync(() => catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelPartialRecovery")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PendingCancellationInitializerLookupRetainsItsOriginalFingerprintAcrossCatalogOwners(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; Guid initializer;
        MirrorPulseRootLabelCancellationRecoveryOwner owner;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
            initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
            owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId))!;
            Assert.IsNotNull(owner); Assert.AreEqual(initializer, owner.InitializationOperationId);
            Assert.AreEqual(64, owner.IntentSha256.Length); Assert.IsTrue(owner.IntentSha256.All(char.IsAsciiHexDigitUpper));
            var intent = (await catalog.ReadNamespaceInitializationAsync(initializer))!;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceInitializationAsync(intent with { PreparedAt = intent.PreparedAt.AddSeconds(1) }));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(owner, await reopened.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        Assert.IsNull((await reopened.ReadNamespaceInitializationStatusAsync(initializer)).Completion);
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelPartialRecovery")]
    [DataRow(true, "omitted")]
    [DataRow(false, "omitted")]
    [DataRow(true, "foreign")]
    [DataRow(false, "foreign")]
    [DataRow(true, "fingerprint")]
    [DataRow(false, "fingerprint")]
    [DataRow(true, "reservation-removed")]
    [DataRow(false, "reservation-removed")]
    public async Task CancellationCannotLoseOrSubstituteItsOriginalPendingInitializerWitness(bool enabled, string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
        var before = await catalog.ReadNamespaceInitializationStatusAsync(initializer);
        var owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId))!;
        var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
        var observation = RootCancellationFromTree(setup, tree);
        var altered = damage switch
        {
            "omitted" => null,
            "foreign" => owner with { InitializationOperationId = Guid.NewGuid() },
            "fingerprint" => owner with { IntentSha256 = new string(owner.IntentSha256[0] == 'A' ? 'B' : 'A', 64) },
            _ => owner
        };
        var cancellation = new MirrorPulseRootLabelUnsealedCancellation(altered is null ? 1 : 2, setup.Root.MemberId, observation,
            new(1, setup.Move.OperationId, observation.PublicEvidenceSha256, tree.Members.Select(PartialRecoveryCancellationEntry).ToArray()))
        { RecoveryOwner = altered };
        if (damage == "reservation-removed")
            await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_initialization_roots WHERE operation_id='{initializer:D}';");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelUnsealedRootLabelAsync(cancellation));
        Assert.AreEqual("Unsealed cancellation cannot replace its original initialization owner.", error.Message);
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        if (damage == "reservation-removed")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceInitializationAsync(initializer));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceInitializationStatusAsync(initializer));
            await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await raw.OpenAsync(); await using var query = raw.CreateCommand();
            query.CommandText = "SELECT payload,fingerprint FROM namespace_initialization_intents WHERE operation_id=$operation;";
            query.Parameters.AddWithValue("$operation", initializer.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(); Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(owner.IntentSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData((byte[])reader[0])));
            Assert.AreEqual(owner.IntentSha256, Convert.ToHexString((byte[])reader[1]));
        }
        else
        {
            Assert.AreEqual(JsonSerializer.Serialize(before.Intent), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationAsync(initializer)));
            Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationStatusAsync(initializer)));
        }
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelPartialRecovery")]
    public async Task CancellationInventoryRefusesChangedOriginalInitializerBytesBeforeUsingAnyMemberProof()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, setup);
        _ = await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId);
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_initialization_intents SET fingerprint=zeroblob(32) WHERE operation_id='{initializer:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
    }

    private static async Task<Guid> PreparePartialCancellationInitializerAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup)
    {
        Guid initializer = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(new(4, initializer,
            [new(setup.Move.RootId, Guid.NewGuid())], setup.Move.PreparedAt.AddSeconds(-2))
        { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory });
        return initializer;
    }

    private static MirrorPulseNamespaceDirectoryMoveEntry PartialRecoveryCancellationEntry(MirrorPulseNamespaceMemberProtectionSnapshot snapshot)
    {
        var context = snapshot.Context; var member = context.Member; var location = context.Location;
        return new(member.MemberId, location.ParentMemberId, location.RelativePath, member.IsDirectory, member.LocalObject, member.OwnerSid,
            context.Protection.ProtectionId, context.Protection.VerifiedAt, location.CompletedMoveOperationId,
            snapshot.Identity, location.RelativePath.Count(character => character == '/'))
        { PreviousPositionOperationId = location.PositionOperationId };
    }
}
