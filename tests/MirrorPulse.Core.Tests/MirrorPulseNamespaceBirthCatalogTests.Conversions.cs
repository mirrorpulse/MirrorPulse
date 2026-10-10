using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryBindingSurvivesOwnersBeforeConversionWithoutInventingOriginalPermissions(bool directory)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection) with { IsDirectory = directory };
        var plan = Plan(birth); var start = Start(plan); var preparation = Conversion(birth, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            Assert.IsNull(await first.ReadNamespaceBirthConversionAsync(birth.OperationId));
            Assert.AreEqual(preparation, await first.PrepareNamespaceBirthConversionAsync(preparation));
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(birth.OperationId));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(preparation, await second.ReadNamespaceBirthConversionAsync(birth.OperationId));
            Assert.AreEqual(preparation, await second.PrepareNamespaceBirthConversionAsync(preparation));
            var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
            var page = await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1);
            Assert.AreEqual(preparation, page.Entries.Single().ConversionPreparation);
            Assert.IsNull(page.Entries.Single().Observation);
            Assert.IsTrue(page.Entries.Single().OwnsNameReservation);
            var observed = Observation(birth, plan, start) with
            {
                LocalObject = preparation.LocalObject,
                BirthDacl = preparation.ExpectedConvertedDacl,
                ObservedAt = preparation.PreparedAt.AddSeconds(1),
            };
            Assert.AreEqual(observed, await second.RecordNamespaceBirthObservationAsync(observed));
            Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
            Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
            Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
            Assert.IsFalse((await second.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
            Assert.AreEqual(preparation, await second.PrepareNamespaceBirthConversionAsync(preparation));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("parent-object")]
    [DataRow("sync-root-object")]
    [DataRow("owner")]
    [DataRow("kind")]
    [DataRow("time")]
    [DataRow("remote-origin")]
    public async Task ConversionPreparationCannotAdoptAnUnrelatedOrdinaryObject(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        if (scenario == "remote-origin") birth = birth with { Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation };
        var plan = Plan(birth); var start = Start(plan); var preparation = Conversion(birth, start);
        var changed = scenario switch
        {
            "volume" => preparation with { LocalObject = preparation.LocalObject with { VolumeSerialNumber = 999 } },
            "sync-root" => preparation with { LocalObject = preparation.LocalObject with { SyncRootFileId = Guid.NewGuid() } },
            "parent-object" => preparation with { LocalObject = birth.ParentLocalObject },
            "sync-root-object" => preparation with { LocalObject = birth.ParentLocalObject with { LocalFileId = birth.ParentLocalObject.SyncRootFileId } },
            "owner" => preparation with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            "kind" => preparation with { IsDirectory = true },
            "time" => preparation with { PreparedAt = start.StartedAt.AddTicks(-1) },
            _ => preparation,
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthConversionAsync(changed));
        Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("birth-dacl")]
    [DataRow("target-dacl")]
    [DataRow("time")]
    public async Task FirstOrdinaryPreparationCannotBeReplacedOrCompleteWithAnotherNativeObject(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var preparation = Conversion(birth, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await catalog.PrepareNamespaceBirthConversionAsync(preparation);
        var changed = scenario switch
        {
            "binding" => preparation with { LocalObject = preparation.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "birth-dacl" => preparation with { BirthDacl = Original },
            "target-dacl" => preparation with { ExpectedConvertedDacl = Original },
            _ => preparation with { PreparedAt = preparation.PreparedAt.AddTicks(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthConversionAsync(changed));
        Assert.AreEqual(preparation, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
        var substituted = Observation(birth, plan, start) with { ObservedAt = preparation.PreparedAt.AddSeconds(1) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(substituted));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryBirthAndOriginalPermissionEvidenceCannotAdoptEachOther(bool originalFirst)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var preparation = Conversion(birth, start);
        var original = parent with { EvidenceId = Guid.NewGuid(), LocalObject = preparation.LocalObject, RelativePath = birth.RelativePath, IsDirectory = false };
        var pretendProtection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        if (originalFirst)
        {
            await catalog.PrepareNamespacePermissionChangeAsync(original, pretendProtection);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthConversionAsync(preparation));
            Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
        }
        else
        {
            await catalog.PrepareNamespaceBirthConversionAsync(preparation);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(original, pretendProtection));
            Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        }
    }

    [TestMethod]
    public async Task LatePreparationCannotBackfillHistoryAfterPlaceholderObservation()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        var preparation = Conversion(birth, start) with { LocalObject = observation.LocalObject };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthConversionAsync(preparation));
        Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("binding-index")]
    [DataRow("reference")]
    public async Task CorruptConversionPreparationIsNotRepairedFromCurrentPaths(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var preparation = Conversion(birth, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            await first.PrepareNamespaceBirthConversionAsync(preparation);
        }
        if (scenario == "reference")
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(preparation with { OwnerSid = "S-1-5-21-100-200-300-1002" });
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "UPDATE namespace_birth_conversions SET payload=$payload,fingerprint=$fingerprint;";
            query.Parameters.AddWithValue("$payload", payload); query.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await query.ExecuteNonQueryAsync();
        }
        else await ExecuteSqlAsync(fixture.Paths, scenario == "fingerprint"
            ? "UPDATE namespace_birth_conversions SET fingerprint=zeroblob(32);"
            : "UPDATE namespace_birth_conversions SET local_file_id='00000000-0000-0000-0000-000000000001';");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthConversionAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.PrepareNamespaceBirthConversionAsync(preparation));
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparationRequiresTheOriginalPlanAndNativeStart(bool planned)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        if (planned) await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceBirthConversionAsync(Conversion(birth, start)));
        Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task PreparedOrdinaryObjectCannotBeAdoptedByAnotherBirthOperation()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var first = Birth(parent, protection);
        var firstPlan = Plan(first); var firstStart = Start(firstPlan); var preparation = Conversion(first, firstStart);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, first, firstPlan, firstStart);
        await catalog.PrepareNamespaceBirthConversionAsync(preparation);
        var second = first with { OperationId = Guid.NewGuid(), RelativePath = "Docs/second.txt" };
        var secondPlan = Plan(second); var secondStart = Start(secondPlan);
        await catalog.PrepareNamespaceBirthAsync(second);
        await catalog.PrepareNamespaceBirthPlanAsync(secondPlan);
        await catalog.RecordNamespaceBirthStartAsync(secondStart);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBirthConversionAsync(
            Conversion(second, secondStart) with { LocalObject = preparation.LocalObject }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(
            Observation(second, secondPlan, secondStart) with { LocalObject = preparation.LocalObject }));
        Assert.IsNull(await catalog.ReadNamespaceBirthConversionAsync(second.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(second.OperationId));
        Assert.AreEqual(preparation, await catalog.ReadNamespaceBirthConversionAsync(first.OperationId));
    }

    [TestMethod]
    public async Task Schema29MigrationPreservesMissingConversionHistory()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(observation);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_conversions; PRAGMA user_version=29;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await second.ReadNamespaceBirthConversionAsync(birth.OperationId));
        Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(plan, await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
        Assert.IsFalse((await second.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
    }

    [TestMethod]
    public async Task FutureCatalogSchemaIsRejectedInsteadOfBeingDowngraded()
    {
        using var fixture = new Fixture();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=31;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(31L, await query.ExecuteScalarAsync());
    }

    private static MirrorPulseNamespaceBirthConversionPreparation Conversion(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceBirthStart start) => new(1, birth.OperationId,
            birth.ParentLocalObject with { LocalFileId = Guid.NewGuid() }, birth.IsDirectory, Owner,
            Dacl($"D:P(A;;FA;;;{Role})"), Protected, 1, start.StartedAt.AddSeconds(1));
}
