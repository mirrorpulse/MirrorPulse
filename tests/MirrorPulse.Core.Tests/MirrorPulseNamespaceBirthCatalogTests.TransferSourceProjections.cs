using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferOriginalSourceProjectionSurvivesStoreOwnersWithoutRecapture(bool nullableFileId)
    {
        using var fixture = new Fixture(); TransferPublicationFixture target; MirrorPulseNamespaceRetirement started;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareAcceptedTransferSourceRetirementAsync(first);
            var proof = FreshTransferRetirementTarget(target); var time = proof.VerifiedAt.AddSeconds(1);
            await first.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
            var projection = TransferSourceProjection(target, time) with { OfficialLocalFileId = nullableFileId ? null : long.MinValue };
            started = await first.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, projection, time);
            Assert.AreEqual(projection, started.TransferSourceProjection);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = started.TransferSourceProjection!;
        Assert.AreEqual(started, await second.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId,
            original with { CapturedAt = original.CapturedAt.AddSeconds(1) }, started.StartedAt!.Value.AddSeconds(1)));
        var deleted = await second.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId,
            TransferRetirementReceipt(target, original.CapturedAt.AddSeconds(2)));
        var completed = await second.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, deleted.NativeDeletedAt!.Value.AddSeconds(1));
        Assert.AreEqual(original, completed.TransferSourceProjection);
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull((await second.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.Acknowledgement);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("operation")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("path")]
    [DataRow("case")]
    [DataRow("revision")]
    [DataRow("binding")]
    [DataRow("before-admission")]
    [DataRow("after-start")]
    public async Task TransferSourceRetirementRejectsForeignOrUnorderedOriginalProjection(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        var prepared = await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var time = proof.VerifiedAt.AddSeconds(1); var original = TransferSourceProjection(target, time);
        var wrong = failure switch
        {
            "version" => original with { Version = 2 },
            "operation" => original with { OperationId = Guid.NewGuid() },
            "item" => original with { ItemId = Guid.NewGuid() },
            "remote" => original with { RemoteId = "another-source" },
            "path" => original with { RelativePath = "Docs/another-source" },
            "case" => original with { RelativePath = original.RelativePath.ToUpperInvariant() },
            "revision" => original with { RemoteRevision = "a-new-revision" },
            "binding" => original with { LocalObject = target.Copy.Plan.TargetParent.Context.Member.LocalObject },
            "before-admission" => original with { CapturedAt = proof.VerifiedAt.AddTicks(-1) },
            _ => original with { CapturedAt = time.AddTicks(1) }
        };
        var bytes = await ReadTransferRetirementBytesAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, wrong, time));
        Assert.AreEqual(bytes, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.AreEqual(prepared, await catalog.ReadNamespaceRetirementAsync(prepared.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("sdk-file-id")]
    [DataRow("sdk-null-file-id")]
    [DataRow("raw-path")]
    public async Task TransferOriginalStartCannotReplaceItsNullableSdkFileIdentifierOrRawPath(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var time = proof.VerifiedAt.AddSeconds(1); var original = TransferSourceProjection(target, time);
        var started = await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, original, time);
        var replacement = failure switch
        {
            "sdk-file-id" => original with { OfficialLocalFileId = original.OfficialLocalFileId + 1 },
            "sdk-null-file-id" => original with { OfficialLocalFileId = null },
            _ => original with { RelativePath = original.RelativePath.Replace('\\', '/') }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, replacement, time));
        Assert.AreEqual(started, await catalog.ReadNamespaceRetirementAsync(started.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferProjectionAndFirstStartRollbackTogetherBeforeAnyNativeEffect(bool cancelled)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        var prepared = await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var time = proof.VerifiedAt.AddSeconds(1); var projection = TransferSourceProjection(target, time);
        var bytes = await ReadTransferRetirementBytesAsync(fixture.Paths);
        if (cancelled)
        {
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, projection, time, stop.Token));
        }
        else
        {
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_source_projection BEFORE UPDATE ON namespace_member_retirements BEGIN SELECT RAISE(ABORT,'original-source-projection-gap'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, projection, time));
            await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_source_projection;");
        }
        Assert.AreEqual(bytes, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.AreEqual(prepared, await catalog.ReadNamespaceRetirementAsync(prepared.OperationId));
        var started = await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, projection, time);
        Assert.AreEqual(projection, started.TransferSourceProjection); Assert.IsNotNull(started.StartedAt);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Schema83RetirementMigrationPreservesLegacyBytesAndCannotInventPreDeletionEvidence(bool settled)
    {
        using var fixture = new Fixture(); TransferPublicationFixture target; DateTimeOffset time;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareAcceptedTransferSourceRetirementAsync(first); var proof = FreshTransferRetirementTarget(target);
            await first.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
            time = proof.VerifiedAt.AddSeconds(1);
            await first.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, time), time);
            if (settled)
            {
                await first.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, TransferRetirementReceipt(target, time.AddSeconds(1)));
                await first.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, time.AddSeconds(2));
            }
        }
        await ChangeTransferRetirementProjectionAsync(fixture.Paths, row =>
        {
            row["Intent"]!["TransferEntry"]!["Version"] = 1;
            row.AsObject().Remove("TransferSourceProjection");
            row.AsObject().Remove("TransferSourceNameEpoch");
        });
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=83;");
        var originalBytes = await ReadTransferRetirementBytesAsync(fixture.Paths);
        await using var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(originalBytes, await ReadTransferRetirementBytesAsync(fixture.Paths));
        var legacy = await migrated.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId);
        Assert.IsNotNull(legacy); Assert.IsNull(legacy.TransferSourceProjection); Assert.AreEqual(time, legacy.StartedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => migrated.StartNamespaceTransferSourceRetirementAsync(
            target.Copy.Plan.OperationId, TransferSourceProjection(target, time.AddSeconds(3)), time.AddSeconds(3)));
        Assert.AreEqual(originalBytes, await ReadTransferRetirementBytesAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(88L, await version.ExecuteScalarAsync());
        Assert.HasCount(2, await migrated.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("binding")]
    [DataRow("late-capture")]
    public async Task TransferRetirementReaderRejectsRehashedLossOfOriginalGenerationEvidence(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var time = proof.VerifiedAt.AddSeconds(1);
        await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, time), time);
        await ChangeTransferRetirementProjectionAsync(fixture.Paths, row =>
        {
            if (failure == "missing") row.AsObject().Remove("TransferSourceProjection");
            else if (failure == "binding") row["TransferSourceProjection"]!["LocalObject"]!["LocalFileId"] = Guid.NewGuid();
            else row["TransferSourceProjection"]!["CapturedAt"] = time.AddTicks(1);
        });
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId));
        StringAssert.Contains(error.Message, failure == "missing" ? "lost its pre-deletion SDK generation" : "original pre-deletion public SDK generation");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var fences = connection.CreateCommand(); fences.CommandText = "SELECT COUNT(*) FROM namespace_transfer_roots;";
        Assert.AreEqual(2L, await fences.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task TransferSourceProjectionCannotGrowAReadableRetirementBeyondItsPersistentBound()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "SELECT length(payload) FROM namespace_member_retirements;";
            long originalLength = (long)(await query.ExecuteScalarAsync())!;
            await ChangeTransferRetirementProjectionAsync(fixture.Paths, row =>
                row["RecoveryReason"] = new string('A', checked((int)(131_000 - originalLength + 2))));
            Assert.AreEqual(131_000L, await query.ExecuteScalarAsync());
        }
        // A bounded historical explanation is readable. Adding a first-start generation must
        // reject an oversized aggregate before any durable start or native side effect.
        var readable = await catalog.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId); Assert.IsNotNull(readable);
        Assert.IsNull(readable.StartedAt); Assert.IsNull(readable.TransferSourceProjection);
        var before = await ReadTransferRetirementBytesAsync(fixture.Paths); var time = proof.VerifiedAt.AddSeconds(1);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.StartNamespaceTransferSourceRetirementAsync(
            target.Copy.Plan.OperationId, TransferSourceProjection(target, time), time));
        Assert.AreEqual(before, await ReadTransferRetirementBytesAsync(fixture.Paths));
        Assert.AreEqual(readable, await catalog.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    private static MirrorPulseNamespaceTransferSourceProjection TransferSourceProjection(TransferPublicationFixture target, DateTimeOffset capturedAt) =>
        new(1, target.Copy.Plan.OperationId, target.Copy.Plan.Source.Identity.ItemId!.Value, target.Copy.Plan.Source.Identity.RemoteId!,
            target.Copy.Plan.Source.Context.Location.RelativePath.Replace('/', '\\'), target.Copy.Plan.SourceRemoteRevision, 42,
            target.Copy.Plan.Source.Context.Member.LocalObject, capturedAt);

    private static async Task<string> ReadTransferRetirementBytesAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_member_retirements;";
        return (string)(await query.ExecuteScalarAsync())!;
    }

    private static async Task ChangeTransferRetirementProjectionAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths, Action<JsonNode> change)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "SELECT payload FROM namespace_member_retirements;";
        var row = JsonNode.Parse((byte[])(await query.ExecuteScalarAsync())!)!; change(row);
        var options = new JsonSerializerOptions { Converters = { new TransferProjectionRootIdConverter() } };
        var retirement = JsonSerializer.Deserialize<MirrorPulseNamespaceRetirement>(row.ToJsonString(), options)!;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retirement, options);
        await using var update = connection.CreateCommand(); update.CommandText = "UPDATE namespace_member_retirements SET payload=$payload,fingerprint=$hash;";
        update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
    }

    private sealed class TransferProjectionRootIdConverter : JsonConverter<RootId>
    {
        public override RootId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => RootId.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RootId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
