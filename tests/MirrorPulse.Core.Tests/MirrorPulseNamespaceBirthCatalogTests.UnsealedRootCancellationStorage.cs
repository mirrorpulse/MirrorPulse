using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    [DataRow("payload")]
    [DataRow("missing-owner")]
    [DataRow("missing-history")]
    [DataRow("missing-marker")]
    [DataRow("rehashed-identity")]
    public async Task DamagedUnsealedTerminalOwnershipCannotSupplyAReadableReceiptOrAnUnfencedRoot(string damage)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseRootLabelUnsealedCancellation cancellation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog); cancellation = await UnsealedCancellationAsync(catalog, setup);
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
        }
        string operation = setup.Move.OperationId.ToString("D");
        string sql = damage switch
        {
            "payload" => "UPDATE root_label_unsealed_cancellations SET payload=x'00';",
            "missing-owner" => "DELETE FROM root_label_unsealed_cancellations;",
            "missing-history" => $"PRAGMA foreign_keys=OFF; DELETE FROM managed_root_rename_history WHERE operation_id='{operation}';",
            "missing-marker" => $"UPDATE managed_root_rename_history SET payload=json_remove(payload,'$.UnsealedCancellationOperationId') WHERE operation_id='{operation}';",
            _ => string.Empty
        };
        if (damage == "rehashed-identity")
        {
            // Preserve the real wire property names while changing only one
            // identity; a recomputed checksum cannot repair lost provenance.
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var query = connection.CreateCommand(); query.CommandText = "SELECT payload FROM root_label_unsealed_cancellations;";
            var record = JsonNode.Parse((byte[])(await query.ExecuteScalarAsync())!)!;
            record["Inventory"]!["Entries"]![1]!["Identity"]!["RemoteId"] = "foreign";
            byte[] payload = Encoding.UTF8.GetBytes(record.ToJsonString());
            sql = $"UPDATE root_label_unsealed_cancellations SET payload=x'{Convert.ToHexString(payload)}',fingerprint=x'{Convert.ToHexString(SHA256.HashData(payload))}';";
        }
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (damage != "missing-owner")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    [DataRow("table")]
    [DataRow("index")]
    [DataRow("index-shape")]
    [DataRow("downgraded-nonempty")]
    public async Task Schema92RefusesLostOrReinterpretedUnsealedOwnershipWithoutRepair(string damage)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRootSubtreeFixtureAsync(catalog);
            _ = await catalog.CancelUnsealedRootLabelAsync(await UnsealedCancellationAsync(catalog, setup));
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "table" => "DROP TABLE root_label_unsealed_cancellations;",
            "index" => "DROP INDEX root_label_unsealed_cancellations_root;",
            "index-shape" => "DROP INDEX root_label_unsealed_cancellations_root; CREATE INDEX root_label_unsealed_cancellations_root ON root_label_unsealed_cancellations(operation_id,root_id);",
            "downgraded-nonempty" => "PRAGMA user_version=91;",
            _ => throw new AssertFailedException("Unknown damage.")
        });
        MirrorPulseProductCatalog? unexpected = null;
        try { await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => unexpected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)); }
        finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    public async Task CleanSchema91MigrationPreservesTheUnsealedOriginalAndItsPublicProofWithoutInventingAnAdmission()
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE root_label_unsealed_cancellations; PRAGMA user_version=91;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Rename, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        CollectionAssert.Contains((await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
        _ = await reopened.CancelUnsealedRootLabelAsync(await UnsealedCancellationAsync(reopened, setup));
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    public async Task UnsealedCancellationReplayCannotRewriteItsOriginalFinalObservation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog); var cancellation = await UnsealedCancellationAsync(catalog, setup);
        _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelUnsealedRootLabelAsync(cancellation with
        { Observation = cancellation.Observation with { VerifiedAt = cancellation.Observation.VerifiedAt.AddSeconds(1) } }));
        await AssertUnsealedCancellationAsync(catalog, setup, cancellation);
    }
}
