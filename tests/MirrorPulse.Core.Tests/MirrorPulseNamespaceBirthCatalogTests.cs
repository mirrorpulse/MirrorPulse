using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Role = "S-1-5-5-123-456";
    private static readonly string Original = Dacl($"D:AI(A;OICI;FA;;;{Owner})");
    private static readonly string Protected = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})");

    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation)]
    public async Task BirthIntentSurvivesOwnersWithoutInventingChildBindingOrOriginalPermissions(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        var birth = Birth(original, protection) with { Origin = origin };
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            Assert.AreEqual(birth, await first.PrepareNamespaceBirthAsync(birth));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(birth, await second.ReadNamespaceBirthAsync(birth.OperationId));
            Assert.AreEqual(birth, await second.PrepareNamespaceBirthAsync(birth));
            Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
            Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
            Assert.IsNull(await second.ReadNamespacePermissionLocalIdentityAsync(original.EvidenceId));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task PendingCaseInsensitiveNameCollisionRollsBackTheSecondIntentAcrossOwners()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            await first.PrepareNamespaceBirthAsync(birth);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var collision = birth with { OperationId = Guid.NewGuid(), RelativePath = "Docs/NEW.TXT" };
        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(() => second.PrepareNamespaceBirthAsync(collision));
        Assert.AreEqual(19, exception.SqliteErrorCode);
        Assert.IsNull(await second.ReadNamespaceBirthAsync(collision.OperationId));
        Assert.AreEqual(birth, await second.ReadNamespaceBirthAsync(birth.OperationId));
        var independent = birth with { OperationId = Guid.NewGuid(), RelativePath = "Docs/other.txt" };
        Assert.AreEqual(independent, await second.PrepareNamespaceBirthAsync(independent));
    }

    [TestMethod]
    public async Task RotationPreservesHistoricalIntentButRejectsStaleNewAdmission()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = Protected,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = birth.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, rotation);
        Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(birth, await catalog.PrepareNamespaceBirthAsync(birth));
        var stale = birth with { OperationId = Guid.NewGuid(), RelativePath = "Docs/stale.txt" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(stale));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(stale.OperationId));
        var current = Birth(original, rotation) with { RelativePath = "Docs/current.txt" };
        Assert.AreEqual(current, await catalog.PrepareNamespaceBirthAsync(current));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth with { RoleSid = rotation.RoleSid }));
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    public async Task ParentRenameCannotBypassThePendingChildNameReservation()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RelativePath = "Renamed",
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = Protected,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = birth.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, rotation);
        var collision = Birth(original, rotation) with { RelativePath = "Renamed/NEW.TXT" };
        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBirthAsync(collision));
        Assert.AreEqual(19, exception.SqliteErrorCode);
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(collision.OperationId));
        Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    public async Task RestoredAndFileParentsCannotAdmitBirths()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        var restore = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = Protected,
            TargetDacl = Original,
            PreparedAt = birth.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, restore);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(Birth(original, restore)));
        var file = Baseline() with { IsDirectory = false }; var fileProtection = Protection(file);
        await ProtectAsync(catalog, file, fileProtection);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(Birth(file, fileProtection)));
    }

    [TestMethod]
    public async Task SyncRootAdmissionRequiresAFirstLevelDirectory()
    {
        using var fixture = new Fixture();
        var baseline = Baseline();
        var original = baseline with
        {
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = baseline.LocalObject with { LocalFileId = baseline.LocalObject.SyncRootFileId }
        };
        var protection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), RootId.New(), original.EvidenceId,
            protection.OperationId, original.LocalObject, "NewRoot", true,
            MirrorPulseNamespaceBirthOrigin.ControlledCreation, Role, protection.PreparedAt.AddSeconds(4));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth with { IsDirectory = false }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth with { RelativePath = "NewRoot/Nested" }));
        Assert.AreEqual(birth, await catalog.PrepareNamespaceBirthAsync(birth));
    }

    [TestMethod]
    public async Task ChangedReplayCannotRewriteAdmissionFieldsOrOriginalParent()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        foreach (var changed in new[]
        {
            birth with { IsDirectory = true }, birth with { RelativePath = "Docs/NEW.TXT" },
            birth with { Origin = MirrorPulseNamespaceBirthOrigin.Import }, birth with { PreparedAt = birth.PreparedAt.AddTicks(1) },
        }) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(changed));
        Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("binding")]
    [DataRow("root")]
    [DataRow("role")]
    [DataRow("nested")]
    [DataRow("time")]
    public async Task ParentAdmissionRejectsUnverifiedOrUnrelatedEvidence(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (scenario == "pending") await catalog.PrepareNamespacePermissionChangeAsync(original, protection);
        else await ProtectAsync(catalog, original, protection);
        var invalid = scenario switch
        {
            "binding" => birth with { ParentLocalObject = birth.ParentLocalObject with { LocalFileId = Guid.NewGuid() } },
            "root" => birth with { RootId = RootId.New() },
            "role" => birth with { RoleSid = "S-1-5-5-123-457" },
            "nested" => birth with { RelativePath = "Docs/absent/new.txt" },
            "time" => birth with { PreparedAt = protection.PreparedAt },
            _ => birth,
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(invalid.OperationId));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("Docs/new.txt:stream")]
    [DataRow("Docs/child/../new.txt")]
    [DataRow("Docs\\new.txt")]
    [DataRow("Docs/new.txt.")]
    public async Task NonCanonicalBirthLocationIsRejectedBeforeCatalogEffects(string path)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection) with { RelativePath = path };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBirthAsync(birth));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task Schema26MigrationPreservesOriginalEvidenceAndCreatesNoHistoricalBirths()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await ProtectAsync(first, original, protection);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_reservations; DROP TABLE namespace_birth_intents; PRAGMA user_version=26;");
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
            Assert.IsNull(await second.ReadNamespaceBirthAsync(Guid.NewGuid()));
        }
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(31L, await query.ExecuteScalarAsync());
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("path-index")]
    [DataRow("parent-reference")]
    public async Task CorruptBirthRecordIsRejectedInsteadOfAdoptingCurrentParent(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            await first.PrepareNamespaceBirthAsync(birth);
        }
        if (scenario == "parent-reference")
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(birth with { ParentLocalObject = birth.ParentLocalObject with { LocalFileId = Guid.NewGuid() } });
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE namespace_birth_intents SET payload=$payload,fingerprint=$fingerprint;";
            command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await command.ExecuteNonQueryAsync();
        }
        else await ExecuteSqlAsync(fixture.Paths, scenario == "fingerprint"
            ? "UPDATE namespace_birth_intents SET fingerprint=zeroblob(32);"
            : "UPDATE namespace_birth_intents SET relative_path_key='DOCS/REPLACEMENT.TXT';");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.PrepareNamespaceBirthAsync(birth));
    }

    private static MirrorPulseNamespacePermissionBaseline Baseline() => new(Guid.NewGuid(), RootId.New(),
        new(123, Guid.NewGuid(), Guid.NewGuid()), "Docs", true, Owner, Original, DateTimeOffset.UtcNow);
    private static MirrorPulseNamespacePermissionIntent Protection(MirrorPulseNamespacePermissionBaseline original) =>
        new(Guid.NewGuid(), original.EvidenceId, original.LocalObject, original.RootId, original.RelativePath,
            MirrorPulseNamespacePermissionChangeKind.Protect, Role, Original, Protected, original.CapturedAt.AddSeconds(1));
    private static MirrorPulseNamespaceBirthIntent Birth(MirrorPulseNamespacePermissionBaseline original,
        MirrorPulseNamespacePermissionIntent protection) => new(1, Guid.NewGuid(), original.RootId!.Value,
            original.EvidenceId, protection.OperationId, original.LocalObject, "Docs/new.txt", false,
            MirrorPulseNamespaceBirthOrigin.ControlledCreation, protection.RoleSid, protection.PreparedAt.AddSeconds(4));
    private static async Task ProtectAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespacePermissionBaseline original,
        MirrorPulseNamespacePermissionIntent protection)
    {
        await catalog.PrepareNamespacePermissionChangeAsync(original, protection);
        await catalog.RecordNamespacePermissionApplicationAsync(protection.OperationId, original.LocalObject, protection.PreparedAt.AddSeconds(1));
        await catalog.VerifyNamespacePermissionChangeAsync(protection.OperationId,
            new(original.LocalObject, Owner, protection.TargetDacl, protection.PreparedAt.AddSeconds(2)));
    }
    private static string Dacl(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);
    private static SqliteConnection Connection(MirrorPulseStoragePaths paths) => new(new SqliteConnectionStringBuilder
    { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-birth-admission", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public Fixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
