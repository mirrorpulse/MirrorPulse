using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("index")]
    [DataRow("missing-proof")]
    [DataRow("changed-intent")]
    [DataRow("ancestor")]
    [DataRow("cycle")]
    public async Task CorruptAcceptedIdentityHistoryCannotSelectANewCurrentObject(string damage)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceIdentityAcceptance first, second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1));
            first = await CorrelateAsync(catalog, setup.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), upload.Proof.OperationId);
            var later = await PrepareIdentityUploadAsync(catalog, setup, root, first.Confirmation.ObservedAt.AddSeconds(1));
            second = await CorrelateAsync(catalog, setup.Member.MemberId, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), later.Proof.OperationId);
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            update.Parameters.AddWithValue("$first", first.Proof.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$second", second.Proof.OperationId.ToString("D"));
            update.CommandText = damage switch
            {
                "fingerprint" => "UPDATE namespace_member_identity_acceptances SET fingerprint=zeroblob(32) WHERE operation_id=$second;",
                "index" => "PRAGMA foreign_keys=OFF; UPDATE namespace_member_identity_acceptances SET member_id=$first WHERE operation_id=$second;",
                "missing-proof" => "PRAGMA foreign_keys=OFF; DELETE FROM content_acceptance_proofs WHERE operation_id=$first;",
                "changed-intent" => "UPDATE mutation_intents SET payload=CAST(json_set(CAST(payload AS TEXT),'$.RootKey','foreign-root') AS BLOB) WHERE operation_id=$first;",
                "ancestor" => "UPDATE namespace_member_identity_acceptances SET fingerprint=zeroblob(32) WHERE operation_id=$first;",
                _ => "UPDATE namespace_member_identity_acceptances SET payload=$payload,fingerprint=$hash,previous_operation_id=$second WHERE operation_id=$second;"
            };
            if (damage == "cycle")
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(second with { PreviousAcceptanceOperationId = second.Proof.OperationId }, BirthProtectionJsonOptions);
                update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            }
            await update.ExecuteNonQueryAsync();
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceIdentityAcceptanceAsync(second.Proof.OperationId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task Schema39UpgradeDoesNotInventIdentityCorrelationFromAnExistingAcceptedUploadOrCurrentPath()
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseContentAcceptanceProof proof;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            proof = (await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1))).Proof;
        }
        string original = await ReadMemberMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_member_identity_acceptances; PRAGMA user_version=39;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var identity = await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        Assert.IsNull(identity.AcceptanceOperationId); Assert.AreEqual(setup.Move.ItemId, identity.ItemId); Assert.AreEqual(setup.Move.RemoteId, identity.RemoteId);
        Assert.AreEqual(proof, await reopened.ReadContentAcceptanceProofAsync(proof.OperationId));
        Assert.AreEqual(original, await ReadMemberMigrationBytesAsync(fixture.Paths));
        Assert.IsNull(await reopened.ReadNamespaceIdentityAcceptanceAsync(proof.OperationId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(93L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA foreign_key_check;"; Assert.IsNull(await query.ExecuteScalarAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
