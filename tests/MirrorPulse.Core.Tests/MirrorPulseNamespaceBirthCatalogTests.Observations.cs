using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualBirthSurvivesOwnersWithoutInventingOriginalPermissionsOrAcceptance(bool directory)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent);
        var birth = Birth(parent, protection) with { IsDirectory = directory };
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(birth.OperationId));
            Assert.AreEqual(observation, await first.RecordNamespaceBirthObservationAsync(observation));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
            Assert.AreEqual(observation, await second.RecordNamespaceBirthObservationAsync(observation));
            Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
            Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
            Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
            Assert.IsFalse((await second.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
            // Recording a physical object does not release its reserved name.
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => second.PrepareNamespaceBirthAsync(
                birth with { OperationId = Guid.NewGuid() }));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("path")]
    [DataRow("kind")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("revision")]
    [DataRow("owner")]
    [DataRow("time")]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("parent-object")]
    [DataRow("sync-root-object")]
    [DataRow("accepted")]
    public async Task WrongActualObjectCannotBecomeBirthEvidence(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var changed = scenario switch
        {
            "root" => observation with { RootId = MirrorPulse.Core.Contracts.RootId.New() },
            "path" => observation with { RelativePath = "Docs/same-name.txt" },
            "kind" => observation with { IsDirectory = true },
            "item" => observation with { ItemId = Guid.NewGuid() },
            "remote" => observation with { RemoteId = "current-path-id" },
            "revision" => observation with { RemoteRevision = "invented-acceptance" },
            "owner" => observation with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            "time" => observation with { ObservedAt = start.StartedAt.AddTicks(-1) },
            "volume" => observation with { LocalObject = observation.LocalObject with { VolumeSerialNumber = 999 } },
            "sync-root" => observation with { LocalObject = observation.LocalObject with { SyncRootFileId = Guid.NewGuid() } },
            "parent-object" => observation with { LocalObject = birth.ParentLocalObject },
            "sync-root-object" => observation with { LocalObject = birth.ParentLocalObject with { LocalFileId = birth.ParentLocalObject.SyncRootFileId } },
            _ => observation with { IsInSync = true },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(changed));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(observation, await catalog.RecordNamespaceBirthObservationAsync(observation));
    }

    [TestMethod]
    [DataRow("ordinary-file")]
    [DataRow("alias")]
    [DataRow("no-link")]
    [DataRow("noncanonical-acl")]
    public async Task UnsafeNativeFactsCannotBeRecorded(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var changed = scenario switch
        {
            "ordinary-file" => observation with { IsPlaceholder = false },
            "alias" => observation with { LinkCount = 2 },
            "no-link" => observation with { LinkCount = 0 },
            _ => observation with { BirthDacl = $"O:{Owner}{Protected}" },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceBirthObservationAsync(changed));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingPlanOrStartCannotBeInferredFromTheObservedObject(bool hasPlan)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, protection); await catalog.PrepareNamespaceBirthAsync(birth);
        if (hasPlan) await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceBirthObservationAsync(Observation(birth, plan, start)));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task TwoBirthsCannotAdoptTheSamePhysicalObject()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        var otherBirth = birth with { OperationId = Guid.NewGuid(), RelativePath = "Docs/other.txt" };
        var otherPlan = Plan(otherBirth); var otherStart = Start(otherPlan);
        await catalog.PrepareNamespaceBirthAsync(otherBirth); await catalog.PrepareNamespaceBirthPlanAsync(otherPlan);
        await catalog.RecordNamespaceBirthStartAsync(otherStart);
        var collision = Observation(otherBirth, otherPlan, otherStart) with { LocalObject = observation.LocalObject };
        var exception = await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.RecordNamespaceBirthObservationAsync(collision));
        Assert.AreEqual(19, exception.SqliteErrorCode);
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(otherBirth.OperationId));
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BornDescriptorAndOriginalBaselineCannotAdoptEachOthersPhysicalObject(bool birthFirst)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var originalFile = Baseline() with
        {
            IsDirectory = false,
            RelativePath = "Other/original.txt",
            LocalObject = observation.LocalObject,
            OriginalDacl = observation.BirthDacl,
            CapturedAt = observation.ObservedAt,
        };
        var originalProtection = Protection(originalFile) with
        {
            ExpectedDacl = originalFile.OriginalDacl,
            TargetDacl = Dacl($"D:P(A;;FR;;;{Owner})(A;;FA;;;{Role})"),
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        if (birthFirst)
        {
            await catalog.RecordNamespaceBirthObservationAsync(observation);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(originalFile, originalProtection));
            Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(originalFile.EvidenceId));
            Assert.IsNull(await catalog.ReadNamespacePermissionChangeAsync(originalProtection.OperationId));
        }
        else
        {
            await catalog.PrepareNamespacePermissionChangeAsync(originalFile, originalProtection);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(observation));
            Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
            Assert.AreEqual(originalFile, await catalog.ReadNamespacePermissionBaselineAsync(originalFile.EvidenceId));
        }
    }

    [TestMethod]
    public async Task FirstNativeFactsRemainHistoricalAcrossParentRotationAndCannotBeReplaced()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = Protected,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = observation.ObservedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, parent, rotation);
        // Recovery may retain actual historical evidence; it does not authorize another creation.
        Assert.AreEqual(observation, await catalog.RecordNamespaceBirthObservationAsync(observation));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(
            observation with { LocalObject = observation.LocalObject with { LocalFileId = Guid.NewGuid() } }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(
            observation with { BirthDacl = rotation.TargetDacl }));
        Assert.IsFalse((await catalog.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("binding-index")]
    [DataRow("reference")]
    public async Task CorruptBirthFactsFailClosedWithoutCurrentPathRepair(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(observation);
        }
        if (scenario == "reference")
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(observation with { OwnerSid = "S-1-5-21-100-200-300-1002" });
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE namespace_birth_observations SET payload=$payload,fingerprint=$fingerprint;";
            command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await command.ExecuteNonQueryAsync();
        }
        else await ExecuteSqlAsync(fixture.Paths, scenario == "fingerprint"
            ? "UPDATE namespace_birth_observations SET fingerprint=zeroblob(32);"
            : "UPDATE namespace_birth_observations SET local_file_id='00000000-0000-0000-0000-000000000001';");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.RecordNamespaceBirthObservationAsync(observation));
    }

    [TestMethod]
    public async Task Schema28MigrationDoesNotInventNativeBirthObservations()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await PrepareStartedBirthAsync(first, parent, protection, birth, plan, start);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_observations; PRAGMA user_version=28;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(plan, await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.AreEqual(start, await second.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
        Assert.IsFalse((await second.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
    }

    private static MirrorPulseNamespaceBirthObservation Observation(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceBirthPlan plan, MirrorPulseNamespaceBirthStart start) =>
        new(1, birth.OperationId, birth.RootId, birth.RelativePath, birth.IsDirectory,
            birth.ParentLocalObject with { LocalFileId = Guid.NewGuid() }, plan.ItemId, plan.RemoteId, plan.RemoteRevision,
            true, false, 1, Owner, Protected, start.StartedAt.AddSeconds(1));

    private static async Task PrepareStartedBirthAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespacePermissionBaseline parent, MirrorPulseNamespacePermissionIntent protection,
        MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan, MirrorPulseNamespaceBirthStart start)
    {
        await ProtectAsync(catalog, parent, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(plan);
        Assert.IsTrue((await catalog.RecordNamespaceBirthStartAsync(start)).NewlyRecorded);
    }
}
