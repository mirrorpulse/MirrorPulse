using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task PlannedIdentityAndStartSurviveOwnersWithoutNativeBirthOrRemoteAcceptance()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            await first.PrepareNamespaceBirthAsync(birth);
            Assert.IsNull(await first.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthStartAsync(birth.OperationId));
            Assert.AreEqual(plan, await first.PrepareNamespaceBirthPlanAsync(plan));
            Assert.IsNull(await first.ReadNamespaceBirthStartAsync(birth.OperationId));
            var begun = await first.RecordNamespaceBirthStartAsync(start);
            Assert.IsTrue(begun.NewlyRecorded);
            Assert.AreEqual(start, begun.Start);
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(plan, await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.AreEqual(start, await second.ReadNamespaceBirthStartAsync(birth.OperationId));
            Assert.AreEqual(plan, await second.PrepareNamespaceBirthPlanAsync(plan));
            var replay = await second.RecordNamespaceBirthStartAsync(start);
            Assert.IsFalse(replay.NewlyRecorded);
            Assert.AreEqual(start, replay.Start);
            Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
            Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("time")]
    public async Task PlannedIdentityCannotBeReselectedAfterStart(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await catalog.RecordNamespaceBirthStartAsync(start);
        var changed = scenario switch
        {
            "item" => plan with { ItemId = Guid.NewGuid() },
            "remote" => plan with { RemoteId = "replacement-object" },
            _ => plan with { PreparedAt = plan.PreparedAt.AddTicks(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(changed));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthStartAsync(start with { StartedAt = start.StartedAt.AddTicks(1) }));
        Assert.AreEqual(plan, await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.AreEqual(start, await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    public async Task LocalBirthPlansCannotInventAnAcceptedRemoteRevision(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection) with { Origin = origin };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(Plan(birth) with { RemoteRevision = "v1" }));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task RemotePopulationPlanRetainsOpaqueRevisionWithoutMutationAcceptance()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection) with { Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation };
        var plan = Plan(birth) with { RemoteRevision = "opaque remote revision" };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        Assert.AreEqual(plan, await catalog.PrepareNamespaceBirthPlanAsync(plan));
        Assert.IsNull(await catalog.ReadMutationAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task DuplicatePlannedItemRollsBackTheNewPlanAndCannotStart()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        var plan = Plan(birth); await catalog.PrepareNamespaceBirthPlanAsync(plan);
        var other = birth with { OperationId = Guid.NewGuid(), RelativePath = "Docs/other.txt" };
        await catalog.PrepareNamespaceBirthAsync(other);
        var collision = Plan(other) with { ItemId = plan.ItemId };
        var exception = await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.PrepareNamespaceBirthPlanAsync(collision));
        Assert.AreEqual(19, exception.SqliteErrorCode);
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(other.OperationId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceBirthStartAsync(Start(collision)));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(other.OperationId));
        Assert.AreEqual(other, await catalog.ReadNamespaceBirthAsync(other.OperationId));
    }

    [TestMethod]
    public async Task ParentRotationBetweenPlanningAndStartingRequiresRecoveryInsteadOfNewAttempt()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(plan);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = Protected,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = plan.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, rotation);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthStartAsync(Start(plan)));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.AreEqual(plan, await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.AreEqual(plan, await catalog.PrepareNamespaceBirthPlanAsync(plan));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingNameReservationBlocksNewPlanOrStartWithoutReacquiringIt(bool hasPlan)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection); var plan = Plan(birth);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            await first.PrepareNamespaceBirthAsync(birth);
            if (hasPlan) await first.PrepareNamespaceBirthPlanAsync(plan);
        }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (hasPlan) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceBirthStartAsync(Start(plan)));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthPlanAsync(plan));
        Assert.IsNull(await second.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.AreEqual(birth, await second.ReadNamespaceBirthAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task HistoricalStartReplayStaysHistoricalAfterParentRotation()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection); await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(plan); await catalog.RecordNamespaceBirthStartAsync(start);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = Protected,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = start.StartedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, rotation);
        var replay = await catalog.RecordNamespaceBirthStartAsync(start);
        Assert.IsFalse(replay.NewlyRecorded);
        Assert.AreEqual(start, replay.Start);
        Assert.AreEqual(start, await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task Schema27MigrationDoesNotInventPlansOrStartsForHistoricalAdmissions()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection); await first.PrepareNamespaceBirthAsync(birth);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_starts; DROP TABLE namespace_birth_plans; PRAGMA user_version=27;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(birth, await second.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.IsNull(await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.IsNull(await second.ReadNamespaceBirthStartAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => second.RecordNamespaceBirthStartAsync(Start(Plan(birth))));
        Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    [DataRow("plan-fingerprint")]
    [DataRow("item-index")]
    [DataRow("start-fingerprint")]
    [DataRow("start-payload")]
    public async Task CorruptPlanOrStartCannotBeReplacedFromCurrentState(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection); await first.PrepareNamespaceBirthAsync(birth);
            await first.PrepareNamespaceBirthPlanAsync(plan); await first.RecordNamespaceBirthStartAsync(start);
        }
        if (scenario == "start-payload")
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start with { StartedAt = plan.PreparedAt.AddSeconds(-1) });
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE namespace_birth_starts SET payload=$payload,fingerprint=$fingerprint;";
            command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await command.ExecuteNonQueryAsync();
        }
        else await ExecuteSqlAsync(fixture.Paths, scenario switch
        {
            "plan-fingerprint" => "UPDATE namespace_birth_plans SET fingerprint=zeroblob(32);",
            "item-index" => "UPDATE namespace_birth_plans SET item_id='00000000-0000-0000-0000-000000000001';",
            _ => "UPDATE namespace_birth_starts SET fingerprint=zeroblob(32);",
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthStartAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.RecordNamespaceBirthStartAsync(start));
        if (scenario is "plan-fingerprint" or "item-index")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.PrepareNamespaceBirthPlanAsync(plan));
    }

    [TestMethod]
    public async Task StartedRecordWithMissingPlanCannotInventOne()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original); var birth = Birth(original, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection); await first.PrepareNamespaceBirthAsync(birth);
            await first.PrepareNamespaceBirthPlanAsync(plan); await first.RecordNamespaceBirthStartAsync(start);
        }
        // Deliberately simulate corruption; production foreign keys prevent this deletion.
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM namespace_birth_plans;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthPlanAsync(plan));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.IsNull(await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalFileAndPlannedBirthCannotAdoptEachOthersIdentity(bool planFirst)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth);
        var file = Baseline() with { IsDirectory = false, RelativePath = "Other/original.txt" };
        var fileProtection = Protection(file);
        var identity = new MirrorPulseNamespacePermissionLocalIdentity(1, file.EvidenceId,
            fileProtection.OperationId, file.LocalObject, plan.ItemId, "original-file-id", fileProtection.PreparedAt);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespacePermissionChangeAsync(file, fileProtection);
        if (planFirst)
        {
            await catalog.PrepareNamespaceBirthPlanAsync(plan);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(identity));
            Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(file.EvidenceId));
            Assert.AreEqual(plan, await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        }
        else
        {
            await catalog.PrepareNamespacePermissionLocalIdentityAsync(identity);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(plan));
            Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.AreEqual(identity, await catalog.ReadNamespacePermissionLocalIdentityAsync(file.EvidenceId));
        }
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared,
            (await catalog.ReadNamespacePermissionChangeAsync(fileProtection.OperationId))!.Phase);
    }

    private static MirrorPulseNamespaceBirthPlan Plan(MirrorPulseNamespaceBirthIntent birth) =>
        new(1, birth.OperationId, Guid.NewGuid(), "planned-local-opaque-id", null, birth.PreparedAt.AddSeconds(1));
    private static MirrorPulseNamespaceBirthStart Start(MirrorPulseNamespaceBirthPlan plan) =>
        new(1, plan.OperationId, plan.PreparedAt.AddSeconds(1));
}
