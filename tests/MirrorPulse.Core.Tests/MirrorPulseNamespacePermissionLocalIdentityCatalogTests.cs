using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseNamespacePermissionLocalIdentityCatalogTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Role = "S-1-5-5-123-456";
    private static readonly string Original = Dacl($"D:AI(A;OICI;FA;;;{Owner})");
    private static readonly string Target = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})");

    [TestMethod]
    public async Task OriginalIdentitySurvivesTwoOwnersAndRoleRotationWithoutRemoteAcceptance()
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline();
        var intent = Intent(baseline);
        var identity = Identity(baseline, intent) with { RemoteId = "known-provider-id" };
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            Assert.AreEqual(identity, await first.PrepareNamespacePermissionLocalIdentityAsync(identity));
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await first.ReadNamespacePermissionChangeAsync(intent.OperationId))!.Phase);
            await CompleteAsync(first, baseline, intent);
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(identity, await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
            Assert.AreEqual(identity, await second.PrepareNamespacePermissionLocalIdentityAsync(identity));
            var rotated = intent with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                ExpectedDacl = Target,
                RoleSid = "S-1-5-5-123-457",
                TargetDacl = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;S-1-5-5-123-457)"),
                PreparedAt = intent.PreparedAt.AddSeconds(10),
            };
            await second.PrepareNamespacePermissionChangeAsync(baseline, rotated);
            await CompleteAsync(second, baseline, rotated);
            Assert.AreEqual(identity, await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
            Assert.AreEqual(identity, await second.PrepareNamespacePermissionLocalIdentityAsync(identity));
            Assert.AreEqual(baseline, await second.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            Assert.HasCount(2, await second.ReadNamespacePermissionChangesAsync());
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("binding")]
    [DataRow("time")]
    public async Task RetainedIdentityCannotBeRechosenAfterRestart(string scenario)
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline();
        var intent = Intent(baseline);
        var identity = Identity(baseline, intent);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await first.PrepareNamespacePermissionLocalIdentityAsync(identity);
        }
        var changed = scenario switch
        {
            "item" => identity with { ItemId = Guid.NewGuid() },
            "remote" => identity with { RemoteId = "replacement" },
            "binding" => identity with { LocalObject = identity.LocalObject with { LocalFileId = Guid.NewGuid() } },
            _ => identity with { PreparedAt = identity.PreparedAt.AddTicks(1) },
        };
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespacePermissionLocalIdentityAsync(changed));
        Assert.AreEqual(identity, await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
    }

    [TestMethod]
    public async Task GlobalItemIdentityCollisionRollsBackAndOriginalIntentRemainsPrepared()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = Baseline(); var firstIntent = Intent(first); var firstIdentity = Identity(first, firstIntent);
        var second = Baseline(); var secondIntent = Intent(second); var secondIdentity = Identity(second, secondIntent);
        await catalog.PrepareNamespacePermissionChangesAsync([new(first, firstIntent), new(second, secondIntent)]);
        await catalog.PrepareNamespacePermissionLocalIdentityAsync(firstIdentity);
        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(
            secondIdentity with { ItemId = firstIdentity.ItemId }));
        Assert.AreEqual(19, exception.SqliteErrorCode);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(second.EvidenceId));
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(secondIntent.OperationId))!.Phase);
        Assert.AreEqual(secondIdentity, await catalog.PrepareNamespacePermissionLocalIdentityAsync(secondIdentity));
        Assert.AreEqual(firstIdentity, await catalog.ReadNamespacePermissionLocalIdentityAsync(first.EvidenceId));
    }

    [TestMethod]
    [DataRow("applied")]
    [DataRow("verified")]
    [DataRow("recovery")]
    [DataRow("rotation")]
    [DataRow("restoration")]
    [DataRow("reprotection")]
    public async Task HistoricalApplicationCannotInventIdentityFromCurrentPath(string scenario)
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline(); var intent = Intent(baseline); var identity = Identity(baseline, intent);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            if (scenario == "recovery")
                await first.RequireNamespacePermissionRecoveryAsync(intent.OperationId, MirrorPulseNamespacePermissionRecoveryReason.Interrupted);
            else if (scenario == "applied")
                await first.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, identity.PreparedAt.AddSeconds(1));
            else
            {
                await CompleteAsync(first, baseline, intent);
                if (scenario is "rotation" or "restoration" or "reprotection")
                {
                    var next = intent with
                    {
                        OperationId = Guid.NewGuid(),
                        ExpectedDacl = Target,
                        PreparedAt = intent.PreparedAt.AddSeconds(10),
                        Kind = scenario == "rotation" ? MirrorPulseNamespacePermissionChangeKind.RotateRole : MirrorPulseNamespacePermissionChangeKind.Restore,
                        RoleSid = scenario == "rotation" ? "S-1-5-5-123-457" : Role,
                        TargetDacl = scenario == "rotation" ? Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;S-1-5-5-123-457)") : Original,
                    };
                    await first.PrepareNamespacePermissionChangeAsync(baseline, next);
                    if (scenario == "reprotection")
                    {
                        await CompleteAsync(first, baseline, next);
                        next = intent with { OperationId = Guid.NewGuid(), PreparedAt = next.PreparedAt.AddSeconds(10) };
                        await first.PrepareNamespacePermissionChangeAsync(baseline, next);
                    }
                    identity = Identity(baseline, next);
                }
            }
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespacePermissionLocalIdentityAsync(identity));
        Assert.IsNull(await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
    }

    [TestMethod]
    public async Task DirectoryAndUnrelatedOriginalCannotReceiveFileIdentity()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var file = Baseline(); var fileIntent = Intent(file);
        var directory = Baseline() with { IsDirectory = true, RelativePath = "Docs" }; var directoryIntent = Intent(directory);
        await catalog.PrepareNamespacePermissionChangesAsync([new(file, fileIntent), new(directory, directoryIntent)]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(Identity(directory, directoryIntent)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(
            Identity(file, fileIntent) with { EvidenceId = directory.EvidenceId }));
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(file.EvidenceId));
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(directory.EvidenceId));
    }

    [TestMethod]
    public async Task RootCaptureBlocksFirstIdentityButCancellationPermitsTheOriginalPreparation()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var file = Baseline(); var intent = Intent(file); var identity = Identity(file, intent);
        await catalog.PrepareNamespacePermissionChangeAsync(file, intent);
        var anchor = file with { EvidenceId = Guid.NewGuid(), IsDirectory = true, RelativePath = "Docs", LocalObject = file.LocalObject with { LocalFileId = Guid.NewGuid() } };
        var anchorIntent = Intent(anchor) with { PreparedAt = intent.PreparedAt.AddSeconds(10) };
        var tree = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), new(anchor, anchorIntent), 1, anchorIntent.PreparedAt);
        await catalog.CreateNamespacePermissionTreeAsync(tree);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(identity));
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(file.EvidenceId));
        await catalog.CancelNamespacePermissionTreeCaptureAsync(tree.ManifestId, tree.CreatedAt.AddSeconds(1));
        Assert.AreEqual(identity, await catalog.PrepareNamespacePermissionLocalIdentityAsync(identity));
    }

    [TestMethod]
    public async Task Schema25UpgradePreservesAllOriginalHistoryAndDoesNotBackfillMissingIdentity()
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline(); var intent = Intent(baseline);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await CompleteAsync(first, baseline, intent);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_permission_local_identities; PRAGMA user_version=25;");
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(baseline, await second.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, (await second.ReadNamespacePermissionChangeAsync(intent.OperationId))!.Phase);
            Assert.IsNull(await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespacePermissionLocalIdentityAsync(Identity(baseline, intent)));
        }
        await using var connection = Connection(fixture.Paths);
        await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(26L, await query.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("payload")]
    [DataRow("item-index")]
    [DataRow("binding")]
    [DataRow("evidence")]
    [DataRow("operation")]
    [DataRow("version")]
    [DataRow("late-time")]
    public async Task CorruptRetainedIdentityCannotBeUsedOnReplay(string scenario)
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline(); var intent = Intent(baseline); var identity = Identity(baseline, intent);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await first.PrepareNamespacePermissionLocalIdentityAsync(identity);
            await CompleteAsync(first, baseline, intent);
        }
        if (scenario == "fingerprint")
            await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_permission_local_identities SET fingerprint=zeroblob(32);");
        else if (scenario == "payload")
            await ReplacePayloadAsync(fixture.Paths, "{"u8.ToArray());
        else if (scenario == "item-index")
            await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_permission_local_identities SET item_id='00000000-0000-0000-0000-000000000001';");
        else
        {
            var corrupt = scenario switch
            {
                "binding" => identity with { LocalObject = identity.LocalObject with { LocalFileId = Guid.NewGuid() } },
                "evidence" => identity with { EvidenceId = Guid.NewGuid() },
                "operation" => identity with { PermissionOperationId = Guid.NewGuid() },
                "version" => identity with { Version = 2 },
                _ => identity with { PreparedAt = intent.PreparedAt.AddDays(1) },
            };
            await ReplacePayloadAsync(fixture.Paths, JsonSerializer.SerializeToUtf8Bytes(corrupt));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.PrepareNamespacePermissionLocalIdentityAsync(identity));
    }

    [TestMethod]
    public async Task IncompleteIdentityAndPreparationPredatingOriginalIntentAreRejected()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline(); var intent = Intent(baseline); var identity = Identity(baseline, intent);
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        foreach (var invalid in new[]
        {
            identity with { Version = 0 }, identity with { ItemId = Guid.Empty }, identity with { EvidenceId = Guid.Empty },
            identity with { PermissionOperationId = Guid.Empty }, identity with { RemoteId = " " }, identity with { RemoteId = new string('x', 4097) },
            identity with { LocalObject = identity.LocalObject with { VolumeSerialNumber = 0 } }, identity with { PreparedAt = default },
        }) await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(invalid));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(
            identity with { PreparedAt = intent.PreparedAt.AddTicks(-1) }));
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
    }

    private static MirrorPulseNamespacePermissionBaseline Baseline() => new(Guid.NewGuid(), RootId.New(),
        new(1, Guid.NewGuid(), Guid.NewGuid()), "Docs/edited.txt", false, Owner, Original, DateTimeOffset.UtcNow);
    private static MirrorPulseNamespacePermissionIntent Intent(MirrorPulseNamespacePermissionBaseline baseline) => new(Guid.NewGuid(),
        baseline.EvidenceId, baseline.LocalObject, baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect,
        Role, Original, Target, baseline.CapturedAt.AddSeconds(1));
    private static MirrorPulseNamespacePermissionLocalIdentity Identity(MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent) => new(1, baseline.EvidenceId, intent.OperationId, baseline.LocalObject,
        Guid.NewGuid(), "local-new-file", intent.PreparedAt.AddSeconds(1));
    private static async Task CompleteAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent)
    {
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt.AddSeconds(2));
        await catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId,
            new(baseline.LocalObject, Owner, intent.TargetDacl, intent.PreparedAt.AddSeconds(3)));
    }
    private static string Dacl(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);
    private static SqliteConnection Connection(MirrorPulseStoragePaths paths) => new(new SqliteConnectionStringBuilder
    { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task ReplacePayloadAsync(MirrorPulseStoragePaths paths, byte[] payload)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE namespace_permission_local_identities SET payload=$payload,fingerprint=$fingerprint;";
        command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
        await command.ExecuteNonQueryAsync();
    }
    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-permission-local-identity", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public CatalogFixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
