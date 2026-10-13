using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task DirectoryMoveSnapshotRetainsMovedDescendantsAndProtectionAcrossOwners()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberRegistration child;
        Guid childMoveId;
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory: true);
            var born = await AddBornAsync(first, NestedBirth(setup.Born, "child.txt", false));
            child = BornMember(born, setup.Member);
            await first.RegisterNamespaceMemberAsync(child);
            var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), child.MemberId,
                born.Protection.ProtectionId, born.Birth.RootId, child.LocalObject, born.Plan.ItemId, born.Plan.RemoteId,
                child.OwnerSid, born.Protection.RoleSid,
                new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, child.RelativePath),
                new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, "Docs/new.txt/renamed.txt"),
                child.RegisteredAt.AddSeconds(1));
            await CompleteMoveChainStepAsync(first, move);
            childMoveId = move.OperationId;
            setup = setup with { Move = setup.Move with { PreparedAt = move.PreparedAt.AddSeconds(10) } };
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
            snapshot = (await first.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId))!;
            Assert.HasCount(2, snapshot.Entries);
            var retained = snapshot.Entries.Single(entry => entry.MemberId == child.MemberId);
            Assert.AreEqual(move.Target.RelativePath, retained.RelativePath);
            Assert.AreEqual(childMoveId, retained.PreviousMoveOperationId);
            Assert.AreEqual(born.Protection.ProtectionId, retained.ProtectionId);
            Assert.AreEqual(born.Plan.ItemId, retained.Identity.ItemId);
            Assert.AreEqual(setup.Move, await first.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
            Assert.AreEqual(child, await first.ReadNamespaceMemberAsync(child.MemberId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = await second.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId);
        Assert.IsNotNull(replay);
        CollectionAssert.AreEqual(snapshot.Entries.ToArray(), replay.Entries.ToArray());
        Assert.AreEqual(snapshot.PublicEvidenceSha256, replay.PublicEvidenceSha256);
        Assert.AreEqual(setup.Move, await second.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.AreEqual(setup.Move, await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(child, await second.ReadNamespaceMemberChildAsync(setup.Member.MemberId, "renamed.txt"));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task DirectoryMoveAdmissionCannotSealADescendantMoveCompletedAfterPreparation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var born = await AddBornAsync(catalog, NestedBirth(setup.Born, "child.txt", false));
        var child = BornMember(born, setup.Member);
        await catalog.RegisterNamespaceMemberAsync(child);
        var childMove = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), child.MemberId,
            born.Protection.ProtectionId, born.Birth.RootId, child.LocalObject, born.Plan.ItemId, born.Plan.RemoteId,
            child.OwnerSid, born.Protection.RoleSid,
            new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, child.RelativePath),
            new(setup.Member.MemberId, setup.Member.LocalObject, setup.Born.Protection.ProtectionId, "Docs/new.txt/renamed.txt"),
            child.RegisteredAt.AddSeconds(1));
        var completed = await CompleteMoveChainStepAsync(catalog, childMove);
        var premature = setup.Move with { PreparedAt = completed.VerifiedAt.AddTicks(-1) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceMemberMoveAsync(premature));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(premature.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(premature.RootId));
        var valid = premature with { PreparedAt = completed.VerifiedAt.AddTicks(1) };
        Assert.AreEqual(valid, await catalog.PrepareNamespaceMemberMoveAsync(valid));
        Assert.IsNotNull(await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(valid.OperationId));
    }

    [TestMethod]
    public async Task DirectoryMoveSnapshotInsertFailureRollsBackIntentReservationAndFence()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory: true);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_directory_snapshot BEFORE INSERT ON namespace_directory_move_snapshots BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => first.PrepareNamespaceMemberMoveAsync(setup.Move));
            Assert.IsNull(await first.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
            Assert.IsNull(await first.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.IsNull(await first.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_directory_snapshot;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Move, await second.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.IsNotNull(await second.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
    }

    [TestMethod]
    public async Task DirectoryMoveAdmissionRejectsAnUnsettledDescendantBirth()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        await catalog.PrepareNamespaceBirthAsync(NestedBirth(setup.Born, "pending.txt", false));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task DirectoryMoveSnapshotReplayDoesNotRefreshAChangedDescendantProtectionEpoch()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var born = await AddBornAsync(catalog, NestedBirth(setup.Born, "child.txt", false));
        await catalog.RegisterNamespaceMemberAsync(BornMember(born, setup.Member));
        var move = setup.Move with { PreparedAt = born.Protection.Verification.ObservedAt.AddSeconds(2) };
        await catalog.PrepareNamespaceMemberMoveAsync(move);
        var snapshot = await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(move.OperationId);
        await catalog.RecordNamespaceBirthProtectionAsync(born.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = born.Protection.ProtectionId,
            Verification = born.Protection.Verification with { ObservedAt = move.PreparedAt.AddSeconds(1) }
        });
        var history = await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(move.OperationId);
        Assert.IsNotNull(history);
        CollectionAssert.AreEqual(snapshot!.Entries.ToArray(), history.Entries.ToArray());
        Assert.AreEqual(move, await catalog.PrepareNamespaceMemberMoveAsync(move));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceMemberMoveExecutionAsync(move.OperationId));
        Assert.AreEqual(move, await catalog.ReadPendingNamespaceMemberMoveAsync(move.RootId));
    }

    [TestMethod]
    public async Task LegacyDirectoryIntentCannotInventItsMissingSubtreeSnapshotOnReplay()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory: true);
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
        }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_directory_move_snapshots;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Move, await second.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.IsNull(await second.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
        Assert.IsNull(await second.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsNotNull(await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    [DataRow("evidence")]
    [DataRow("depth")]
    [DataRow("duplicate")]
    [DataRow("protection")]
    [DataRow("binding")]
    [DataRow("identity")]
    public async Task DirectoryMoveSnapshotRejectsCorruptionEvenWithARecomputedFingerprint(string corruption)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory: true);
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
            snapshot = (await first.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId))!;
        }
        var root = snapshot.Entries[0];
        var changed = corruption switch
        {
            "evidence" => snapshot with { PublicEvidenceSha256 = new string('0', 64) },
            "depth" => snapshot with { Entries = [root with { Depth = 1 }] },
            "duplicate" => snapshot with { Entries = [root, root] },
            "protection" => snapshot with { Entries = [root with { ProtectionVerifiedAt = root.ProtectionVerifiedAt.AddTicks(-1) }] },
            "binding" => snapshot with { Entries = [root with { LocalObject = root.LocalObject with { LocalFileId = Guid.NewGuid() } }] },
            _ => snapshot with { Entries = [root with { Identity = root.Identity with { RemoteId = "unproved:other" } }] }
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed);
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_directory_move_snapshots SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$id", setup.Move.OperationId.ToString("D"));
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
    }
}
