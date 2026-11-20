using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task Schema87MigrationPreservesLegacyRemoteRetirementWithoutInventingSuccessorAuthority()
    {
        using var fixture = new Fixture(); RootRegistration root; MirrorPulseNamespaceRetirement terminal; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalRemoteRetirementAsync(catalog, legacy: true); root = prepared.Root;
            await catalog.RecordNamespaceRetirementStartAsync(prepared.Intent.OperationId, prepared.Intent.PreparedAt, prepared.Intent.RemoteEntry!.ItemId);
            terminal = await catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(prepared.Intent, false));
            Assert.IsNull(terminal.RemoteSourceNameEpoch);
            original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX namespace_remote_retirement_items; PRAGMA user_version=87;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.AreEqual(terminal, await reopened.ReadNamespaceRetirementAsync(terminal.OperationId));
        var resolver = new MirrorPulseRemoteIdentityResolver(reopened);
        var current = await resolver.ReadCurrentAsync(root, "backend-file", "accepted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(root, current).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resolver.ResolveAsync(root, "backend-file", "later-v2").AsTask());
        Assert.IsNull(await reopened.ReadRemoteIdentityGenerationAsync(root.RootId, current.ItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Schema88RejectsMissingOrChangedRemoteRetirementItemIndex(bool changed)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, changed
            ? "DROP INDEX namespace_remote_retirement_items; CREATE INDEX namespace_remote_retirement_items ON namespace_member_retirements(operation_id);"
            : "DROP INDEX namespace_remote_retirement_items;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task OrdinaryRemoteGenerationLookupUsesItsPublicItemIndexInsteadOfScanningRetirementHistory()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = """
            EXPLAIN QUERY PLAN SELECT operation_id FROM namespace_member_retirements INDEXED BY namespace_remote_retirement_items
            WHERE json_extract(CAST(payload AS TEXT),'$.Intent.RemoteEntry.ItemId')='item'
                AND json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NOT NULL AND phase<>4
            ORDER BY operation_id LIMIT 2;
            """;
        var rows = new List<string>(); await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(3));
        Assert.HasCount(1, rows); StringAssert.StartsWith(rows[0], "SEARCH namespace_member_retirements USING INDEX namespace_remote_retirement_items");
    }
}
