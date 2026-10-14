using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CurrentInitializationCompletesMovedBirthAndRetiredHistoryWithoutRewritingFirstFacts()
    {
        using var fixture = new Fixture(); Guid initialization = Guid.NewGuid(), anchor;
        MirrorPulseNamespaceInitializationCompletion completed; MirrorPulseNamespaceCurrentTreeSnapshot inventory;
        MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareCurrentMovableMemberAsync(catalog); anchor = setup.ParentMember.ParentMemberId!.Value;
            var deleted = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/deleted.txt" });
            var deletedMember = BornMember(deleted, setup.ParentMember); await catalog.RegisterNamespaceMemberAsync(deletedMember);
            var retirement = Retire(setup, Guid.NewGuid()) with
            { MemberId = deletedMember.MemberId, LocalObject = deletedMember.LocalObject, RelativePath = deletedMember.RelativePath };
            await catalog.PrepareNamespaceRetirementAsync(retirement);
            await catalog.RecordNamespaceRetirementStartAsync(retirement.OperationId, retirement.PreparedAt);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceRetirementAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(2));
            await CompleteMoveChainStepAsync(catalog, setup.Move);
            await PrepareCurrentInitializationAsync(catalog, setup, initialization);
            inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor);
            // Synthetic observations validate transaction correlation; they are not native audit evidence.
            completed = await catalog.CompleteNamespaceCurrentInitializationAsync(initialization, inventory, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, completed.InventoryKind);
            Assert.AreEqual(inventory.Fingerprint, completed.CatalogFingerprint); Assert.AreEqual(3, completed.MemberCount);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
            Assert.AreEqual(setup.Born.Observation, await catalog.ReadNamespaceBirthObservationAsync(setup.Member.OriginId));
            Assert.AreEqual("Docs/moved.txt", (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
            Assert.HasCount(1, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor)).RetiredMembers);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceInitializationAsync(initialization, inventory.Members, completed.CompletedAt));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var next = new MirrorPulseNamespaceInitializationIntent(1, Guid.NewGuid(), [new(setup.Move.RootId, Guid.NewGuid())], completed.CompletedAt.AddSeconds(1));
        await reopened.PrepareNamespaceInitializationAsync(next);
        Assert.AreEqual(completed, await reopened.CompleteNamespaceCurrentInitializationAsync(initialization, inventory, completed.CompletedAt.AddSeconds(2)));
        Assert.AreEqual(completed, (await reopened.ReadNamespaceInitializationStatusAsync(initialization)).Completion);
        CollectionAssert.AreEqual(new[] { setup.Move.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsNull(await reopened.ReadNamespaceInitializationCompletionAsync(next.OperationId));
    }

    [TestMethod]
    [DataRow("insert")]
    [DataRow("release")]
    public async Task CurrentInitializationReceiptAndOwnedFenceReleaseRollbackTogether(string point)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCurrentMovableMemberAsync(catalog); Guid initialization = Guid.NewGuid();
        await PrepareCurrentInitializationAsync(catalog, setup, initialization);
        var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.ParentMemberId!.Value);
        string trigger = point == "insert" ? "BEFORE INSERT ON namespace_initialization_completions" : "BEFORE DELETE ON namespace_initialization_roots";
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER fail_current_completion {trigger} BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(initialization, inventory, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(initialization));
        Assert.AreEqual(inventory.Fingerprint, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(inventory.AnchorMemberId)).Fingerprint);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_current_completion;");
        await catalog.CompleteNamespaceCurrentInitializationAsync(initialization, inventory, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task CurrentInitializationRejectsStaleInventoryAndPendingWorkHiddenByTheSameRootFence()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCurrentMovableMemberAsync(catalog); Guid initialization = Guid.NewGuid();
        await PrepareCurrentInitializationAsync(catalog, setup, initialization);
        var before = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.ParentMemberId!.Value);
        var unregistered = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/pending.txt" });
        // FencedRoots is still the same set; unpublished work must not be masked by the initializer.
        var pending = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(before.AnchorMemberId);
        CollectionAssert.AreEqual(before.FencedRoots.ToArray(), pending.FencedRoots.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(initialization, pending, DateTimeOffset.UtcNow.AddMinutes(1)));
        await catalog.RegisterNamespaceMemberAsync(BornMember(unregistered, setup.ParentMember));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(initialization, before, DateTimeOffset.UtcNow.AddMinutes(1)));
        var current = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(before.AnchorMemberId);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(initialization, current, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(initialization));
    }

    [TestMethod]
    public async Task CurrentInitializationRejectsMissingOriginalCaptureDespiteAConsistentRegisteredGraph()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCurrentMovableMemberAsync(catalog); Guid initialization = Guid.NewGuid();
        await catalog.PrepareNamespaceInitializationAsync(RecoveryAdmission(initialization, setup.Move.RootId));
        var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.ParentMemberId!.Value);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(initialization, inventory, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(initialization));
    }

    [TestMethod]
    public async Task Schema51OriginalCompletionPayloadAndFingerprintRemainUnchangedDuringCurrentInventoryMigration()
    {
        using var fixture = new Fixture(); Guid initialization = Guid.NewGuid(); MirrorPulseNamespaceInitializationCompletion receipt;
        byte[] payload, fingerprint;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareCurrentMovableMemberAsync(catalog);
            await PrepareCurrentInitializationAsync(catalog, setup, initialization);
            // Retire the sole born child so the original-only completion can exercise the legacy payload shape.
            var retirement = Retire(setup, Guid.NewGuid()); await catalog.PrepareNamespaceRetirementAsync(retirement);
            await catalog.RecordNamespaceRetirementStartAsync(retirement.OperationId, retirement.PreparedAt);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceRetirementAsync(retirement.OperationId, retirement.PreparedAt.AddSeconds(2));
            var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.ParentMember.ParentMemberId!.Value);
            receipt = await catalog.CompleteNamespaceInitializationAsync(initialization, inventory.Members, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory, receipt.InventoryKind);
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "SELECT payload,fingerprint FROM namespace_initialization_completions;";
            await using (var reader = await query.ExecuteReaderAsync()) { Assert.IsTrue(await reader.ReadAsync()); payload = (byte[])reader[0]; fingerprint = (byte[])reader[1]; }
            Assert.DoesNotContain(System.Text.Encoding.UTF8.GetString(payload), "inventoryKind");
            query.CommandText = "PRAGMA user_version=51;"; await query.ExecuteNonQueryAsync();
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(receipt, await reopened.ReadNamespaceInitializationCompletionAsync(initialization));
            Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var after = Connection(fixture.Paths); await after.OpenAsync(); await using var read = after.CreateCommand();
        read.CommandText = "SELECT payload,fingerprint FROM namespace_initialization_completions;";
        await using var row = await read.ExecuteReaderAsync(); Assert.IsTrue(await row.ReadAsync());
        CollectionAssert.AreEqual(payload, (byte[])row[0]); CollectionAssert.AreEqual(fingerprint, (byte[])row[1]);
    }

    private static async Task PrepareCurrentInitializationAsync(MirrorPulseProductCatalog catalog, MoveFixture setup, Guid initialization)
    {
        var tree = (await catalog.ReadNamespacePermissionTreesAsync(setup.Move.RootId)).Single();
        await catalog.PrepareNamespaceInitializationAsync(new(1, initialization, [new(setup.Move.RootId, tree.Definition.ManifestId)], DateTimeOffset.UtcNow));
    }

    private static async Task<MoveFixture> PrepareCurrentMovableMemberAsync(MirrorPulseProductCatalog catalog)
    {
        var parent = Baseline(); var proof = Protection(parent);
        var preparation = new MirrorPulseNamespacePermissionPreparation(parent, proof);
        var definition = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), preparation, 1, parent.CapturedAt);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [new(0, null, preparation)]);
        await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, proof.PreparedAt.AddSeconds(1));
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof));
        var member = BornMember(born, parentMember); await catalog.RegisterNamespaceMemberAsync(member);
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), member.MemberId, born.Protection.ProtectionId,
            born.Birth.RootId, member.LocalObject, born.Plan.ItemId, born.Plan.RemoteId, member.OwnerSid, born.Protection.RoleSid,
            new(parentMember.MemberId, parentMember.LocalObject, proof.OperationId, member.RelativePath),
            new(parentMember.MemberId, parentMember.LocalObject, proof.OperationId, "Docs/moved.txt"), member.RegisteredAt.AddSeconds(1));
        return new(parent, proof, parentMember, born, member, move);
    }
}
