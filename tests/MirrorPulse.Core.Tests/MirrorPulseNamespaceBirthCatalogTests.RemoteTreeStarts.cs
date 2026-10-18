using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private static readonly JsonSerializerOptions TreeStartFixtureOptions = new()
    { Converters = { new TreeStartRootIdFixtureConverter() } };

    [TestMethod]
    public async Task OriginalRemoteTreeStartRetainsItsFirstInvocationAcrossPagesAndCatalogOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch original;
        MirrorPulseNamespaceRemoteRetirementTreeStart expected;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteTreeAsync(first, 65);
            original = await first.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
                setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
            Assert.IsNull(await first.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
            byte[] originalHeader = await ReadTreeStartHeaderBytesAsync(fixture.Paths, original.OperationId);
            expected = await first.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt.AddSeconds(1));
            byte[] invokedHeader = await ReadTreeStartHeaderBytesAsync(fixture.Paths, original.OperationId);
            Assert.IsTrue(invokedHeader.Length > originalHeader.Length && invokedHeader.Length <= originalHeader.Length + 128);
            string startHash = (await first.ReadNamespaceRetirementBatchAsync(original.OperationId))!.RemoteTreeStartFingerprint!;
            CollectionAssert.AreEqual(originalHeader, Encoding.UTF8.GetBytes(RemoveTreeStartHeaderReference(Encoding.UTF8.GetString(invokedHeader), startHash)));
            Assert.AreEqual(original.OperationId, expected.OperationId); Assert.AreEqual(original.RemoteEntry!.BatchFingerprint, expected.BatchFingerprint);
            Assert.AreEqual(original.RemoteTreeFingerprint, expected.TreeFingerprint);
            Assert.AreEqual(expected, await first.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, expected.StartedAt.AddMinutes(1)));
            Assert.HasCount(64, (await first.ReadNamespaceRemoteRetirementTreePageAsync(original.OperationId)).Entries);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, await second.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        Assert.AreEqual(expected, await second.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, expected.StartedAt.AddHours(1)));
        Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Prepared, (await second.ReadNamespaceRetirementBatchAsync(original.OperationId))!.Phase);
        Assert.HasCount(2, (await second.ReadNamespaceRemoteRetirementTreePageAsync(original.OperationId, 63)).Entries);
        Assert.IsTrue(await second.CanResumeNamespaceRemoteRetirementTreeAsync((await second.ReadNamespaceRetirementBatchAsync(original.OperationId))!));
        CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), original.RootId);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalRemoteTreeStartRollbackDoesNotInventAnInvocation(bool afterStartInsert)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER abort_tree_start BEFORE " +
            (afterStartInsert ? "UPDATE ON namespace_member_retirement_batches" : "INSERT ON namespace_remote_retirement_tree_starts") +
            " BEGIN SELECT RAISE(ABORT,'synthetic start failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        Assert.IsTrue(await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER abort_tree_start;");
        var start = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt.AddSeconds(1));
        Assert.AreEqual(start, await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    [DataRow("checksum")]
    [DataRow("operation")]
    [DataRow("batch")]
    [DataRow("tree")]
    [DataRow("chronology")]
    [DataRow("version")]
    [DataRow("json")]
    [DataRow("extra-field")]
    public async Task OriginalRemoteTreeStartRejectsCorruptOrReassignedInvocationHistory(string corrupt)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        var first = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt);
        var changed = corrupt switch
        {
            "operation" => first with { OperationId = Guid.NewGuid() },
            "batch" => first with { BatchFingerprint = new string('B', 64) },
            "tree" => first with { TreeFingerprint = new string('B', 64) },
            "chronology" => first with { StartedAt = original.PreparedAt.AddTicks(-1) },
            "version" => first with { Version = 2 },
            _ => first
        };
        byte[] originalPayload = JsonSerializer.SerializeToUtf8Bytes(first);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(changed);
        if (corrupt == "json") payload = Encoding.UTF8.GetBytes("{invalid");
        if (corrupt == "extra-field") payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)[..^1] + ",\"extra\":true}");
        byte[] hash = corrupt == "checksum" ? new byte[32] : SHA256.HashData(payload);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_remote_retirement_tree_starts SET payload=X'" + Convert.ToHexString(payload) +
            "',fingerprint=X'" + Convert.ToHexString(hash) + "';");
        if (corrupt != "checksum") await RewriteTreeStartHeaderHashAsync(fixture.Paths, original.OperationId,
            Convert.ToHexString(SHA256.HashData(originalPayload)), Convert.ToHexString(hash));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt.AddSeconds(1)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("missing-row")]
    [DataRow("payload")]
    public async Task OriginalRemoteTreeCannotStartWithAnIncompleteOrCorruptSealedManifest(string corrupt)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, corrupt switch
        {
            "owner" => "UPDATE namespace_remote_retirement_tree_owners SET change_id='changed';",
            "missing-row" => "DELETE FROM namespace_remote_retirement_tree_entries WHERE ordinal=1;",
            _ => "UPDATE namespace_remote_retirement_tree_entries SET payload=x'7b7d' WHERE ordinal=1;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt));
        Assert.AreEqual(0L, await ReadTreeStartCountAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("descendant")]
    [DataRow("parent")]
    public async Task OriginalRemoteTreeCannotBeginAfterCurrentMemberNamesAreLost(string missing)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        Guid member = missing == "root" ? original.MemberId : missing == "descendant" ? original.Entries[1].MemberId :
            original.RemoteEntry!.Parent.Context.Member.MemberId;
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_member_names WHERE member_id='" + member.ToString("D") + "';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("default-time")]
    [DataRow("early-time")]
    public async Task OriginalRemoteTreeStartRequiresItsOriginalAdmissionAndChronology(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(
            invalid == "operation" ? Guid.Empty : original.OperationId,
            invalid == "default-time" ? default : invalid == "early-time" ? original.PreparedAt.AddTicks(-1) : original.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    public async Task OriginalRemoteTreeStartCannotCreateAnUnknownOperation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Guid operation = Guid.NewGuid();
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(operation));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(operation, DateTimeOffset.UtcNow));
        Assert.AreEqual(0L, await ReadTreeStartCountAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task ControlledRetirementCannotAcquireAnOriginalSdkInvocationStart()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 1);
        var controlled = await catalog.PrepareNamespaceRetirementBatchAsync(setup.Intent.OperationId, setup.Intent.MemberId);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(controlled.OperationId, controlled.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(controlled.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForeignControlledRetirementFenceCannotBeIgnoredByAnOriginalTreeStart(bool alreadyStarted)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        var before = alreadyStarted ? await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt) : null;
        // Inject a valid second product owner to model interrupted/older admission code.
        // Native and SDK state are not involved, and neither owner may be ignored.
        var foreign = new MirrorPulseNamespaceRetirementBatch(1, Guid.NewGuid(), original.MemberId, original.RootId,
            original.Entries, original.PreparedAt);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(foreign, TreeStartFixtureOptions);
        await ExecuteSqlAsync(fixture.Paths, "INSERT INTO namespace_member_retirement_batches(operation_id,member_id,root_id,payload,fingerprint,phase) VALUES('" +
            foreign.OperationId.ToString("D") + "','" + foreign.MemberId.ToString("D") + "','" + foreign.RootId + "',X'" + Convert.ToHexString(payload) +
            "',X'" + Convert.ToHexString(SHA256.HashData(payload)) + "',0);");
        Assert.IsNotNull(await catalog.ReadNamespaceRetirementBatchAsync(foreign.OperationId));
        Assert.IsFalse(await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt));
        Assert.AreEqual(before, await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    [DataRow("record")]
    [DataRow("header-reference")]
    [DataRow("different-reference")]
    public async Task OriginalRemoteTreeStartCannotLoseEitherSideOfItsCommittedInvocation(string missing)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        var start = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt);
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(start)));
        if (missing == "record") await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM namespace_remote_retirement_tree_starts;");
        else await RewriteTreeStartHeaderHashAsync(fixture.Paths, original.OperationId, hash, missing == "header-reference" ? null : new string('B', 64));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRetirementBatchAsync(original.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt.AddSeconds(1)));
    }

    [TestMethod]
    public async Task OriginalTreeStartSerializesReadersAndRejectsAConcurrentCatalogOwner()
    {
        using var fixture = new Fixture();
        await using var writer = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(writer, 65);
        var original = await writer.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
            setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        });
        var invocation = Task.Run(() => writer.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt));
        for (int index = 0; index < 64; index++)
        {
            var observed = (await writer.ReadNamespaceRetirementBatchAsync(original.OperationId))!;
            Assert.AreEqual(original.RemoteTreeFingerprint, observed.RemoteTreeFingerprint);
        }
        Assert.AreEqual(await invocation, await writer.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    public async Task Schema71RemoteTreeAdmissionGainsNoInventedStartDuringUpgrade()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) original = await PrepareTreeStartFixtureAsync(first);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_retirement_tree_starts; PRAGMA user_version=71;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await second.ReadNamespaceRetirementBatchAsync(original.OperationId))!;
        Assert.AreEqual(original.RemoteEntry, retained.RemoteEntry); Assert.AreEqual(original.RemoteTreeFingerprint, retained.RemoteTreeFingerprint);
        Assert.IsNull(await second.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        Assert.IsTrue(await second.CanResumeNamespaceRemoteRetirementTreeAsync(retained));
        var start = await second.RecordNamespaceRemoteRetirementTreeStartAsync(original.OperationId, original.PreparedAt.AddSeconds(1));
        Assert.AreEqual(start, await second.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
    }

    [TestMethod]
    public async Task CancelledOriginalTreeStartPreservesTheAdmissionWithoutInvokingSdk()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareTreeStartFixtureAsync(catalog);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.RecordNamespaceRemoteRetirementTreeStartAsync(
            original.OperationId, original.PreparedAt, cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStartAsync(original.OperationId));
        Assert.IsTrue(await catalog.CanResumeNamespaceRemoteRetirementTreeAsync(original));
    }

    private static async Task<MirrorPulseNamespaceRetirementBatch> PrepareTreeStartFixtureAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareRemoteTreeAsync(catalog, 1);
        return await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
            setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
    }

    private static async Task<long> ReadTreeStartCountAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.ProductCatalogDatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM namespace_remote_retirement_tree_starts;";
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private static async Task RewriteTreeStartHeaderHashAsync(MirrorPulseStoragePaths paths, Guid operation, string oldHash, string? newHash)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT payload FROM namespace_member_retirement_batches WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operation.ToString("D"));
        string original = Encoding.UTF8.GetString((byte[])(await query.ExecuteScalarAsync())!);
        string fragment = "\"RemoteTreeStartFingerprint\":\"" + oldHash + "\"";
        if (!original.Contains(fragment, StringComparison.Ordinal)) throw new InvalidOperationException("The test requires the original committed header reference.");
        string changed = newHash is not null ? original.Replace(fragment, "\"RemoteTreeStartFingerprint\":\"" + newHash + "\"", StringComparison.Ordinal) :
            RemoveTreeStartHeaderReference(original, oldHash);
        byte[] payload = Encoding.UTF8.GetBytes(changed);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$id", operation.ToString("D")); update.Parameters.AddWithValue("$payload", payload);
        update.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await update.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadTreeStartHeaderBytesAsync(MirrorPulseStoragePaths paths, Guid operation)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.ProductCatalogDatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT payload FROM namespace_member_retirement_batches WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operation.ToString("D"));
        return (byte[])(await query.ExecuteScalarAsync())!;
    }

    private static string RemoveTreeStartHeaderReference(string header, string hash)
    {
        string fragment = "\"RemoteTreeStartFingerprint\":\"" + hash + "\"";
        return header.Contains(fragment + ",", StringComparison.Ordinal) ? header.Replace(fragment + ",", string.Empty, StringComparison.Ordinal) :
            header.Replace("," + fragment, string.Empty, StringComparison.Ordinal);
    }

    private sealed class TreeStartRootIdFixtureConverter : JsonConverter<RootId>
    {
        public override RootId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => RootId.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RootId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
