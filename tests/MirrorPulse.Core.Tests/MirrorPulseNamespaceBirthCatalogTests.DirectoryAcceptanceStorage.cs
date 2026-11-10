using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task Schema104AddsOnlyEmptyDirectoryOwnershipAndPreservesLegacyMemberIdentity()
    {
        using var fixture = new Fixture(); MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            setup = await PrepareMovableMemberAsync(catalog, directory: true);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE directory_acceptances; PRAGMA user_version=103;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var identity = await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        Assert.AreEqual(setup.Born.Plan.ItemId, identity.ItemId);
        Assert.IsNull(identity.AcceptanceOperationId);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("missing-table")]
    [DataRow("missing-index")]
    [DataRow("wrong-index")]
    [DataRow("downgraded-nonempty")]
    public async Task DirectorySchemaRefusesMissingAlteredOrDowngradedOriginalOwnership(string loss)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog, directory: true);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            await PrepareDirectoryIdentityAsync(catalog, setup, root);
        }
        string sql = loss switch
        {
            "missing-table" => "DROP TABLE directory_acceptances;",
            "missing-index" => "DROP INDEX ux_directory_identity_first;",
            "wrong-index" => "DROP INDEX ix_directory_identity_member; CREATE INDEX ix_directory_identity_member ON directory_acceptances(operation_id);",
            "downgraded-nonempty" => "PRAGMA user_version=103;",
            _ => throw new InvalidOperationException(),
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        MirrorPulseProductCatalog? unexpected = null;
        try { await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => unexpected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)); }
        finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("proof")]
    [DataRow("receipt")]
    [DataRow("member-index")]
    [DataRow("predecessor-index")]
    public async Task DirectoryIdentityReaderRefusesCorruptedPayloadOrIndexedHistory(string loss)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseDirectoryAcceptance proof;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog, directory: true);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            proof = await PrepareDirectoryIdentityAsync(catalog, setup, root);
            await catalog.RecordDirectoryConfirmationAsync(DirectoryReceipt(proof, setup.Member.RegisteredAt.AddSeconds(1)));
        }
        string sql = loss switch
        {
            "proof" => "UPDATE directory_acceptances SET payload=zeroblob(length(payload));",
            "receipt" => "UPDATE directory_acceptances SET receipt=zeroblob(length(receipt));",
            "member-index" => $"UPDATE directory_acceptances SET member_id='{setup.ParentMember.MemberId:D}';",
            "predecessor-index" => $"UPDATE directory_acceptances SET previous_operation_id='{proof.OperationId:D}';",
            _ => throw new InvalidOperationException(),
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadDirectoryConfirmationAsync(proof.OperationId));
        if (loss != "member-index")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
