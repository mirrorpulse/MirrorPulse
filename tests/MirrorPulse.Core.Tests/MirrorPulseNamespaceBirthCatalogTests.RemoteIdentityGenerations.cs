using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task RemoteIdentityGenerationSurvivesOwnersWithoutChangingOriginalRetirementOrAcknowledgement()
    {
        using var fixture = new Fixture(); MirrorPulseRemoteIdentityGeneration selected; MirrorPulseNamespaceTransferCopy copy; string original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            copy = await PrepareCompletedRetiredTransferAsync(first); selected = await GenerationAfterTransferAsync(first, copy);
            original = await ReadTransferRetirementBytesAsync(fixture.Paths);
            Assert.IsNull(await first.ReadRemoteIdentityGenerationAsync(selected.RootId, selected.BaseItemId));
            Assert.AreEqual(selected, await first.SelectRemoteIdentityGenerationAsync(selected));
            Assert.AreEqual(selected, await first.SelectRemoteIdentityGenerationAsync(selected));
            Assert.AreEqual(copy, await first.ReadNamespaceTransferAsync(copy.Plan.OperationId));
            Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
            Assert.IsEmpty(await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(selected, await second.ReadRemoteIdentityGenerationAsync(selected.RootId, selected.BaseItemId));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.AreEqual(copy, await second.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM remote_identity_generation_history;";
        Assert.AreEqual(1L, await count.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("root")]
    [DataRow("root-key")]
    [DataRow("base-item")]
    [DataRow("base-remote")]
    [DataRow("previous-item")]
    [DataRow("previous-remote")]
    [DataRow("previous-generation")]
    [DataRow("retirement")]
    [DataRow("name-epoch")]
    [DataRow("before-ack")]
    public async Task RemoteIdentityGenerationRejectsForeignOrUnsettledPredecessor(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var selection = await GenerationAfterTransferAsync(catalog, copy);
        var wrong = failure switch
        {
            "instance" => selection with { InstanceId = InstanceId.New() },
            "root" => selection with { RootId = RootId.New() },
            "root-key" => selection with { RootKey = "another-root" },
            "base-item" => selection with { BaseItemId = Guid.NewGuid() },
            "base-remote" => selection with { BaseRemoteId = "another-base" },
            "previous-item" => selection with { PreviousItemId = Guid.NewGuid() },
            "previous-remote" => selection with { PreviousRemoteId = "another-source" },
            "previous-generation" => selection with { PreviousGeneration = Guid.NewGuid() },
            "retirement" => selection with { RetirementOperationId = Guid.NewGuid() },
            "name-epoch" => selection with { RetiredNameEpoch = selection.RetiredNameEpoch + 1 },
            _ => selection with { SelectedAt = copy.Acknowledgement!.AcknowledgedAt!.Value.AddTicks(-1) }
        };
        var original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(wrong));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(wrong.RootId, wrong.BaseItemId));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.AreEqual(copy, await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId));
    }

    [TestMethod]
    [DataRow("empty-generation")]
    [DataRow("empty-item")]
    [DataRow("original-item")]
    [DataRow("original-address")]
    [DataRow("oversized-address")]
    public async Task RemoteIdentityGenerationRejectsInvalidOrReusedNewAddress(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var selection = await GenerationAfterTransferAsync(catalog, copy);
        var wrong = failure switch
        {
            "empty-generation" => selection with { Generation = Guid.Empty },
            "empty-item" => selection with { ItemId = Guid.Empty },
            "original-item" => selection with { ItemId = selection.BaseItemId },
            "original-address" => selection with { RemoteId = selection.BaseRemoteId },
            _ => selection with { RemoteId = new string('a', 16_385) }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.SelectRemoteIdentityGenerationAsync(wrong));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
    }

    [TestMethod]
    public async Task RemoteIdentityGenerationDoesNotAdoptTargetOrSelectBeforeOriginalAck()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareSettledTransferAsync(catalog); var copy = (await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!;
        var selection = await GenerationAfterTransferAsync(catalog, copy);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(selection));
        var started = await StartTransferAckAsync(catalog, target);
        copy = await catalog.CompleteNamespaceTransferAcknowledgementAsync(started.Acknowledgement!, started.Acknowledgement!.StartedAt.AddSeconds(1));
        selection = await GenerationAfterTransferAsync(catalog, copy);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(selection with { ItemId = target.Publication.ItemId }));
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
    }

    [TestMethod]
    public async Task RemoteIdentityGenerationCompetingSelectionCannotReplaceOrForkFirstSuccessor()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var first = await GenerationAfterTransferAsync(catalog, copy);
        var competing = first with { Generation = Guid.NewGuid(), ItemId = Guid.NewGuid(), RemoteId = "another-successor" };
        await catalog.SelectRemoteIdentityGenerationAsync(first);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(competing));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SelectRemoteIdentityGenerationAsync(first with { SelectedAt = first.SelectedAt.AddTicks(1) }));
        Assert.AreEqual(first, await catalog.ReadRemoteIdentityGenerationAsync(first.RootId, first.BaseItemId));
    }

    [TestMethod]
    public async Task RemoteIdentityGenerationPointerFailureRollsBackHistoryAndAllowsExactRetry()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var selection = await GenerationAfterTransferAsync(catalog, copy);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER generation_pointer_fault BEFORE INSERT ON remote_identity_generations BEGIN SELECT RAISE(ABORT,'generation pointer fault'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SelectRemoteIdentityGenerationAsync(selection));
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM remote_identity_generation_history;"; Assert.AreEqual(0L, await count.ExecuteScalarAsync());
        }
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER generation_pointer_fault;");
        Assert.AreEqual(selection, await catalog.SelectRemoteIdentityGenerationAsync(selection));
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("indexed-item")]
    public async Task RemoteIdentityGenerationRejectsCorruptHistory(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareCompletedRetiredTransferAsync(catalog); var selection = await GenerationAfterTransferAsync(catalog, copy);
        await catalog.SelectRemoteIdentityGenerationAsync(selection);
        await ExecuteSqlAsync(fixture.Paths, failure == "payload" ? "UPDATE remote_identity_generation_history SET payload=X'7B7D';" :
            "UPDATE remote_identity_generation_history SET item_id='00000000-0000-0000-0000-000000000001';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRemoteIdentityGenerationAsync(selection.RootId, selection.BaseItemId));
    }

    [TestMethod]
    public async Task RemoteIdentityGenerationSchema85MigrationPreservesOriginalPayloadAndRequiresTablesAt86()
    {
        using var fixture = new Fixture(); string original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareCompletedRetiredTransferAsync(first); original = await ReadTransferRetirementBytesAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE remote_identity_generations; DROP TABLE remote_identity_generation_history; PRAGMA user_version=85;");
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE remote_identity_generations;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task RemoteIdentityGenerationCurrentLookupUsesOnlyIndexedAddressAndHistory()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN SELECT h.payload FROM remote_identity_generations c
            LEFT JOIN remote_identity_generation_history h ON h.generation=c.generation
            WHERE c.root_id='root' AND c.base_item_id='item';
            """;
        var rows = new List<string>(); await using var reader = await plan.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(3));
        Assert.HasCount(2, rows); Assert.IsTrue(rows.All(row => row.StartsWith("SEARCH ", StringComparison.Ordinal)));
        Assert.IsTrue(rows.Any(row => row.StartsWith("SEARCH c USING INDEX ", StringComparison.Ordinal) &&
            row.Contains("(root_id=? AND base_item_id=?)", StringComparison.Ordinal)), string.Join("; ", rows));
        Assert.IsTrue(rows.Any(row => row.StartsWith("SEARCH h USING INDEX ", StringComparison.Ordinal) &&
            row.Contains("(generation=?)", StringComparison.Ordinal)), string.Join("; ", rows));
    }

    private static async Task<MirrorPulseRemoteIdentityGeneration> GenerationAfterTransferAsync(
        MirrorPulseProductCatalog catalog, MirrorPulseNamespaceTransferCopy copy)
    {
        var retirement = (await catalog.ReadNamespaceRetirementAsync(copy.Plan.DeleteOperationId))!;
        return new(1, copy.Plan.SourceRoute.RootId, copy.Plan.SourceRoute.InstanceId, copy.Plan.SourceRoute.RootKey,
            copy.Plan.Source.Identity.ItemId!.Value, copy.Plan.Source.Identity.RemoteId!, null, Guid.NewGuid(),
            copy.Plan.Source.Identity.ItemId.Value, copy.Plan.Source.Identity.RemoteId!, Guid.NewGuid(), "opaque-successor-address",
            copy.Plan.OperationId, retirement.OperationId, retirement.TransferSourceNameEpoch!.Value,
            copy.Acknowledgement?.AcknowledgedAt?.AddSeconds(1) ?? retirement.CompletedAt!.Value.AddSeconds(20));
    }
}
