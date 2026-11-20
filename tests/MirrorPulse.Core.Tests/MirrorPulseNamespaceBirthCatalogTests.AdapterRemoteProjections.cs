using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task AdapterRemoteProjectionRetainsExactInputAndPayloadAcrossOwnersAndIncomingCompletion()
    {
        using var fixture = new Fixture(); var instance = InstanceId.New();
        var projection = new MirrorPulseAdapterRemoteProjection(instance, instance + "/original", new byte[32], [1, 2, 3]);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.RetainAdapterRemoteProjectionAsync(projection); await first.RetainAdapterRemoteProjectionAsync(projection);
            var incoming = new MirrorPulseRetainedRemoteBatch(instance, projection.BatchId, [9, 8, 7]);
            await first.RetainRemoteBatchAsync(incoming); await first.CompleteRetainedRemoteBatchAsync(incoming);
            Assert.IsNull(await first.ReadRetainedRemoteBatchAsync(instance));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = (await reopened.ReadAdapterRemoteProjectionAsync(instance, projection.BatchId))!;
        CollectionAssert.AreEqual(projection.SourceFingerprint, original.SourceFingerprint); CollectionAssert.AreEqual(projection.Payload, original.Payload);
        Assert.IsNull(await reopened.ReadAdapterRemoteProjectionAsync(instance, instance + "/other"));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("input")]
    [DataRow("payload")]
    public async Task AdapterRemoteProjectionRejectsChangedReplayWithoutReplacingOriginal(string fault)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var instance = InstanceId.New(); var original = new MirrorPulseAdapterRemoteProjection(instance, instance + "/original", new byte[32], [1, 2, 3]);
        await catalog.RetainAdapterRemoteProjectionAsync(original);
        var changed = fault == "input" ? original with { SourceFingerprint = Enumerable.Repeat((byte)1, 32).ToArray() } : original with { Payload = [4] };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RetainAdapterRemoteProjectionAsync(changed));
        var restored = (await catalog.ReadAdapterRemoteProjectionAsync(instance, original.BatchId))!;
        CollectionAssert.AreEqual(original.Payload, restored.Payload); CollectionAssert.AreEqual(original.SourceFingerprint, restored.SourceFingerprint);
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("batch")]
    [DataRow("fingerprint")]
    [DataRow("empty-payload")]
    [DataRow("large-payload")]
    public async Task AdapterRemoteProjectionRequiresOriginalBoundedInstanceInputAndPayload(string fault)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var instance = InstanceId.New(); var original = new MirrorPulseAdapterRemoteProjection(instance, instance + "/original", new byte[32], [1]);
        var invalid = fault switch
        {
            "instance" => original with { InstanceId = InstanceId.New() },
            "batch" => original with { BatchId = instance + "/" + new string('b', 16_384) },
            "fingerprint" => original with { SourceFingerprint = new byte[31] },
            "empty-payload" => original with { Payload = [] },
            _ => original with { Payload = new byte[32 * 1024 * 1024 + 1] }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RetainAdapterRemoteProjectionAsync(invalid));
        Assert.IsNull(await catalog.ReadAdapterRemoteProjectionAsync(instance, original.BatchId));
    }

    [TestMethod]
    public async Task AdapterRemoteProjectionRejectsCorruptPayloadAndUsesItsCompositeIndexedLookup()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var instance = InstanceId.New(); var original = new MirrorPulseAdapterRemoteProjection(instance, instance + "/original", new byte[32], [1]);
        await catalog.RetainAdapterRemoteProjectionAsync(original);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE adapter_remote_projections SET payload=X'02';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadAdapterRemoteProjectionAsync(instance, original.BatchId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN SELECT payload FROM adapter_remote_projections WHERE instance_id='instance' AND batch_id='batch';";
        await using var reader = await query.ExecuteReaderAsync(); Assert.IsTrue(await reader.ReadAsync());
        string plan = reader.GetString(3); Assert.IsTrue(plan.StartsWith("SEARCH adapter_remote_projections USING INDEX ", StringComparison.Ordinal), plan);
        Assert.IsTrue(plan.Contains("(instance_id=? AND batch_id=?)", StringComparison.Ordinal), plan); Assert.IsFalse(await reader.ReadAsync());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    public async Task AdapterRemoteProjectionSchema86UpgradePreservesOldHistoryAndSchema87RequiresItsTable(string fault)
    {
        using var fixture = new Fixture(); string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { await PrepareCompletedRetiredTransferAsync(catalog); original = await ReadTransferRetirementBytesAsync(fixture.Paths); }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE adapter_remote_projections; PRAGMA user_version=86;");
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
        await ExecuteSqlAsync(fixture.Paths, fault == "missing" ? "DROP TABLE adapter_remote_projections;" :
            "DROP TABLE adapter_remote_projections; CREATE TABLE adapter_remote_projections(instance_id TEXT, batch_id TEXT, payload BLOB);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        Assert.AreEqual(original, await ReadTransferRetirementBytesAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task AdapterRemoteProjectionRejectsPopulatedFutureTableInSchema86()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { var instance = InstanceId.New(); await catalog.RetainAdapterRemoteProjectionAsync(new(instance, instance + "/original", new byte[32], [1])); }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=86;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }
}
