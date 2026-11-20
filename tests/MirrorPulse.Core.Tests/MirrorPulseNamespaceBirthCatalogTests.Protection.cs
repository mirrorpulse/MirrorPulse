using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private static readonly JsonSerializerOptions BirthProtectionJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BirthProtectionSurvivesOwnersWithoutReplacingHistoricalBirthOrOriginalPermissions(bool directory)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent);
        var birth = Birth(parent, parentProtection) with { IsDirectory = directory };
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var protection = BirthProtection(observation, parentProtection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, parentProtection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(observation);
            Assert.IsNull(await first.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
            Assert.AreEqual(protection, await first.RecordNamespaceBirthProtectionAsync(protection));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(protection, await second.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(protection, await second.RecordNamespaceBirthProtectionAsync(protection));
        Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
        Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        var entry = (await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1)).Entries.Single();
        Assert.AreEqual(protection, entry.LatestProtection);
        Assert.IsTrue(entry.OwnsNameReservation);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task PlannedOrStartedBirthCannotInventProtectionWithoutItsOriginalNativeObservation()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceBirthProtectionAsync(BirthProtection(observation, parentProtection)));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(plan, await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("time")]
    public async Task ProtectionCannotAdoptAnotherObjectOwnerOrPredateTheOriginalObservation(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var protection = BirthProtection(observation, parentProtection);
        protection = protection with
        {
            Verification = scenario switch
            {
                "binding" => protection.Verification with { LocalObject = protection.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } },
                "owner" => protection.Verification with { OwnerSid = "S-1-5-21-100-200-300-1002" },
                _ => protection.Verification with { ObservedAt = observation.ObservedAt.AddTicks(-1) },
            },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(protection));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("ordinary")]
    [DataRow("aliases")]
    [DataRow("no-link")]
    [DataRow("self-predecessor")]
    [DataRow("empty-predecessor")]
    public async Task IncompleteOrUnsafeProtectionFactsAreRejectedBeforePersistence(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var protection = BirthProtection(observation, parentProtection);
        protection = scenario switch
        {
            "version" => protection with { Version = 2 },
            "ordinary" => protection with { IsPlaceholder = false },
            "aliases" => protection with { LinkCount = 2 },
            "no-link" => protection with { LinkCount = 0 },
            "self-predecessor" => protection with { PreviousProtectionId = protection.ProtectionId },
            _ => protection with { PreviousProtectionId = Guid.Empty },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceBirthProtectionAsync(protection));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task ParentRotationRequiresANewEpochWhilePreservingTheOldProtectionAndFirstBirth()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var first = BirthProtection(observation, parentProtection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        await catalog.RecordNamespaceBirthProtectionAsync(first);
        var rotation = parentProtection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-321-654",
            ExpectedDacl = parentProtection.TargetDacl,
            TargetDacl = parentProtection.TargetDacl.Replace(Role, "S-1-5-5-321-654", StringComparison.Ordinal),
            PreparedAt = first.Verification.ObservedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, parent, rotation);
        Assert.AreEqual(first, await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(first, await catalog.RecordNamespaceBirthProtectionAsync(first));
        var stale = first with { ProtectionId = Guid.NewGuid(), PreviousProtectionId = first.ProtectionId };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(stale));
        var current = first with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = first.ProtectionId,
            ParentPermissionOperationId = rotation.OperationId,
            RoleSid = rotation.RoleSid,
            Verification = first.Verification with { Dacl = rotation.TargetDacl, ObservedAt = rotation.PreparedAt.AddSeconds(3) },
        };
        Assert.AreEqual(current, await catalog.RecordNamespaceBirthProtectionAsync(current));
        Assert.AreEqual(current, await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(first, await catalog.RecordNamespaceBirthProtectionAsync(first));
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(parent, await catalog.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
    }

    [TestMethod]
    public async Task EpochForksAndChangedReplayCannotOverwriteTheLatestProtection()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var first = BirthProtection(observation, parentProtection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        var missing = first with { PreviousProtectionId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(missing));
        await catalog.RecordNamespaceBirthProtectionAsync(first);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(first with { LinkCount = 1, Verification = first.Verification with { ObservedAt = first.Verification.ObservedAt.AddTicks(1) } }));
        var second = first with { ProtectionId = Guid.NewGuid(), PreviousProtectionId = first.ProtectionId };
        await catalog.RecordNamespaceBirthProtectionAsync(second);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(second with { ProtectionId = Guid.NewGuid() }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(second with { ProtectionId = Guid.NewGuid(), PreviousProtectionId = null }));
        Assert.AreEqual(second, await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task AnotherVerifiedParentCannotClaimTheBornChild()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        var other = Baseline(); var otherProtection = Protection(other);
        await ProtectAsync(catalog, other, otherProtection);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(
            BirthProtection(observation, parentProtection) with { ParentPermissionOperationId = otherProtection.OperationId }));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("index")]
    [DataRow("canonical-reference")]
    public async Task CorruptProtectionIsRejectedAfterCatalogReopen(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var protection = BirthProtection(observation, parentProtection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, parentProtection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(observation);
            await first.RecordNamespaceBirthProtectionAsync(protection);
        }
        if (scenario == "canonical-reference")
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(protection with { RoleSid = "S-1-5-5-321-654" }, BirthProtectionJsonOptions);
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var change = connection.CreateCommand();
            change.CommandText = "UPDATE namespace_birth_protections SET payload=$payload,fingerprint=$fingerprint;";
            change.Parameters.AddWithValue("$payload", payload); change.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await change.ExecuteNonQueryAsync();
        }
        else await ExecuteSqlAsync(fixture.Paths, scenario == "fingerprint"
            ? "UPDATE namespace_birth_protections SET fingerprint=zeroblob(32);"
            : "UPDATE namespace_birth_protections SET protection_id='00000000-0000-0000-0000-000000000001';");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
    }

    [TestMethod]
    public async Task Schema30MigrationDoesNotInventCurrentBirthProtection()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(first, parent, parentProtection, birth, plan, start);
            await first.RecordNamespaceBirthObservationAsync(observation);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_protections; PRAGMA user_version=30;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await second.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        Assert.IsNull((await second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 1)).Entries.Single().LatestProtection);
    }

    [TestMethod]
    public async Task ParentRestorationDoesNotMakeHistoricalChildProtectionCurrentOrPermitANewEpoch()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var first = BirthProtection(observation, parentProtection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, parentProtection, birth, plan, start);
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        await catalog.RecordNamespaceBirthProtectionAsync(first);
        var restoration = parentProtection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = parentProtection.TargetDacl,
            TargetDacl = parent.OriginalDacl,
            PreparedAt = first.Verification.ObservedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, parent, restoration);
        var invalid = first with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = first.ProtectionId,
            ParentPermissionOperationId = restoration.OperationId,
            Verification = first.Verification with { ObservedAt = restoration.PreparedAt.AddSeconds(3) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(invalid));
        Assert.AreEqual(first, await catalog.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(first, await catalog.RecordNamespaceBirthProtectionAsync(first));
        Assert.AreEqual(observation, await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task ConcurrentVerificationsCannotForkTheSameBirthProtectionEpochAcrossRestart()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProtection = Protection(parent); var birth = Birth(parent, parentProtection);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var first = BirthProtection(observation, parentProtection);
        MirrorPulseNamespaceBirthProtection? winner;
        await using (var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStartedBirthAsync(owner, parent, parentProtection, birth, plan, start);
            await owner.RecordNamespaceBirthObservationAsync(observation);
            await owner.RecordNamespaceBirthProtectionAsync(first);
            var leftEpoch = first with { ProtectionId = Guid.NewGuid(), PreviousProtectionId = first.ProtectionId };
            var rightEpoch = leftEpoch with { ProtectionId = Guid.NewGuid() };
            var results = await Task.WhenAll(AttemptAsync(owner, leftEpoch), AttemptAsync(owner, rightEpoch));
            Assert.HasCount(1, results.OfType<MirrorPulseNamespaceBirthProtection>());
            winner = results.Single(result => result is not null);
            Assert.AreEqual(winner, await owner.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        }
        await using var next = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(winner, await next.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(observation, await next.ReadNamespaceBirthObservationAsync(birth.OperationId));

        static Task<MirrorPulseNamespaceBirthProtection?> AttemptAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceBirthProtection epoch) =>
            Task.Run(async () =>
            {
                try { return await catalog.RecordNamespaceBirthProtectionAsync(epoch); }
                catch (InvalidOperationException) { return null; }
            });
    }

    private static MirrorPulseNamespaceBirthProtection BirthProtection(MirrorPulseNamespaceBirthObservation observation,
        MirrorPulseNamespacePermissionIntent parent) => new(1, Guid.NewGuid(), observation.OperationId, null,
            parent.OperationId, parent.RoleSid, new(observation.LocalObject, observation.OwnerSid,
                observation.BirthDacl, observation.ObservedAt.AddSeconds(1)), true, 1);
}
