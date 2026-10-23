using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OriginalFileMoveUsesTheRetainedIdentityWithoutRewritingItsBaseline()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var original = setup.Parent with
        {
            EvidenceId = Guid.NewGuid(),
            IsDirectory = false,
            RelativePath = "Docs/original.bin",
            LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var proof = Protection(original);
        await catalog.PrepareNamespacePermissionChangeAsync(original, proof);
        var identity = new MirrorPulseNamespacePermissionLocalIdentity(1, original.EvidenceId, proof.OperationId,
            original.LocalObject, Guid.NewGuid(), "local:original", proof.PreparedAt.AddSeconds(1));
        await catalog.PrepareNamespacePermissionLocalIdentityAsync(identity);
        await catalog.RecordNamespacePermissionApplicationAsync(proof.OperationId, original.LocalObject, proof.PreparedAt.AddSeconds(1));
        await catalog.VerifyNamespacePermissionChangeAsync(proof.OperationId,
            new(original.LocalObject, original.OwnerSid, proof.TargetDacl, proof.PreparedAt.AddSeconds(2)));
        var member = OriginalMember(original, proof, setup.ParentMember.MemberId);
        await catalog.RegisterNamespaceMemberAsync(member);
        var move = setup.Move with
        {
            MemberId = member.MemberId,
            ProtectionId = proof.OperationId,
            LocalObject = member.LocalObject,
            ItemId = identity.ItemId,
            RemoteId = identity.RemoteId,
            Source = setup.Move.Source with { RelativePath = member.RelativePath },
            PreparedAt = new[] { member.RegisteredAt, setup.Member.RegisteredAt }.Max().AddSeconds(1)
        };
        Assert.AreEqual(move, await catalog.PrepareNamespaceMemberMoveAsync(move));
        Assert.AreEqual(move, await catalog.ReadNamespaceMemberMoveAsync(move.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.AreEqual(identity, await catalog.ReadNamespacePermissionLocalIdentityAsync(original.EvidenceId));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberByBindingAsync(member.LocalObject));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("canonical")]
    [DataRow("root-index")]
    [DataRow("parent-reference")]
    [DataRow("pending-index")]
    [DataRow("reservation")]
    [DataRow("start-fingerprint")]
    public async Task MoveRecoveryRejectsDamagedEvidenceInsteadOfRepairingItFromCurrentNames(string damage)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            await first.PrepareNamespaceMemberMoveAsync(setup.Move);
            await first.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = damage switch
            {
                "fingerprint" => "UPDATE namespace_member_moves SET fingerprint=zeroblob(32);",
                "canonical" => "UPDATE namespace_member_moves SET payload=$payload,fingerprint=$fingerprint;",
                "root-index" => "UPDATE namespace_member_moves SET root_id='00000000-0000-0000-0000-000000000001';",
                "parent-reference" => "UPDATE namespace_member_moves SET source_parent_id=$member,payload=$payload,fingerprint=$fingerprint;",
                "pending-index" => "UPDATE namespace_member_move_pending SET member_id=$member;",
                "reservation" => "UPDATE namespace_member_move_reservations SET child_name_key='OTHER';",
                _ => "UPDATE namespace_member_move_starts SET fingerprint=zeroblob(32);"
            };
            if (damage == "canonical")
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(setup.Move, BirthProtectionJsonOptions) + " ");
                command.Parameters.AddWithValue("$payload", bytes); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(bytes));
            }
            if (damage == "parent-reference")
            {
                var altered = setup.Move with { Source = setup.Move.Source with { ParentMemberId = setup.Member.MemberId, ParentLocalObject = setup.Member.LocalObject } };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(altered, BirthProtectionJsonOptions);
                command.Parameters.AddWithValue("$payload", bytes); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(bytes));
                command.Parameters.AddWithValue("$member", setup.Member.MemberId.ToString("D"));
            }
            if (damage == "pending-index") command.Parameters.AddWithValue("$member", setup.ParentMember.MemberId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (damage == "start-fingerprint")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    public async Task ConcurrentMoveAdmissionsKeepExactlyOneOriginalIntentAndReservation()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var other = setup.Move with { OperationId = Guid.NewGuid(), Target = setup.Move.Target with { RelativePath = "Docs/another" } };
        var results = await Task.WhenAll(AdmitAsync(setup.Move), AdmitAsync(other));
        Assert.AreEqual(1, results.Count(value => value));
        var pending = await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId); Assert.IsNotNull(pending);
        Assert.AreEqual(results[0] ? setup.Move : other, pending);
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(results[0] ? other.OperationId : setup.Move.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));

        async Task<bool> AdmitAsync(MirrorPulseNamespaceMemberMoveIntent intent)
        {
            try { await catalog.PrepareNamespaceMemberMoveAsync(intent); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    [TestMethod]
    public async Task Schema35MigrationPreservesOriginalNamesAndBirthEvidenceWithoutInventingAMove()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { setup = await PrepareMovableMemberAsync(first); }
        string previous = await ReadMemberMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_member_move_starts; DROP TABLE namespace_member_move_reservations; DROP TABLE namespace_member_move_pending; DROP TABLE namespace_member_moves; PRAGMA user_version=35;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(previous, await ReadMemberMigrationBytesAsync(fixture.Paths));
        Assert.AreEqual(setup.Member, await current.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.IsNull(await current.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM namespace_member_moves;"; Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(87L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA foreign_key_check;"; Assert.IsNull(await query.ExecuteScalarAsync());
    }
}
