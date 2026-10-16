using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    public async Task RecoveryVerificationSurvivesOwnersWithoutPublishingAnObservedOrProtectedBirth(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection) with { Origin = origin };
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection); var facts = RecoveryVerification(action, plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            await first.PrepareNamespaceBirthRecoveryActionAsync(action);
            Assert.IsNull(await first.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
            Assert.AreEqual(facts, await first.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(facts, await second.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.AreEqual(facts, await second.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        var entry = (await second.ReadNamespaceBirthRecoveryPageAsync(await second.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.AreEqual(action, entry.LatestRecoveryAction); Assert.AreEqual(facts, entry.LatestRecoveryVerification);
        Assert.AreEqual(conversion, entry.ConversionPreparation);
        Assert.IsNull(entry.Observation); Assert.IsNull(entry.LatestProtection); Assert.IsTrue(entry.OwnsNameReservation);
        Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
        Assert.IsNull(await second.ReadNamespacePermissionBaselineAsync(birth.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("time")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("revision")]
    [DataRow("descriptor")]
    [DataRow("birth")]
    public async Task RecoveryVerificationCannotAdoptCurrentPathIdentityOrUnrelatedNativeFacts(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection); var facts = RecoveryVerification(action, plan);
        var invalid = scenario switch
        {
            "binding" => facts with { Verification = facts.Verification with { LocalObject = facts.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "owner" => facts with { Verification = facts.Verification with { OwnerSid = "S-1-5-21-100-200-300-9999" } },
            "time" => facts with { Verification = facts.Verification with { ObservedAt = action.PreparedAt.AddTicks(-1) } },
            "item" => facts with { ItemId = Guid.NewGuid() },
            "remote" => facts with { RemoteId = "unrelated-remote-id" },
            "revision" => facts with { RemoteRevision = "unaccepted-revision" },
            "descriptor" => facts with { Verification = facts.Verification with { Dacl = action.RoleOnlyDacl } },
            _ => facts with { BirthOperationId = Guid.NewGuid() },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.AreEqual(conversion, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("in-sync")]
    [DataRow("links")]
    public async Task RecoveryVerificationRequiresAnUnacceptedSingleLinkPlaceholder(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection); var facts = RecoveryVerification(action, plan);
        var invalid = scenario switch
        {
            "ordinary" => facts with { IsPlaceholder = false },
            "in-sync" => facts with { IsInSync = true },
            _ => facts with { LinkCount = 2 },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
    }

    [TestMethod]
    public async Task MissingRecoveryIntentCannotBeReconstructedFromNativeVerification()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(action, plan)));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
    }

    [TestMethod]
    public async Task SupersededRecoveryCannotRecordNewFactsForAnOlderAction()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var first = RecoveryAction(conversion, protection);
        var next = first with { RecoveryId = Guid.NewGuid(), PreviousRecoveryId = first.RecoveryId, PreparedAt = first.PreparedAt.AddSeconds(2) };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(first); await catalog.PrepareNamespaceBirthRecoveryActionAsync(next);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(first, plan)));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(first.RecoveryId));
        Assert.AreEqual(RecoveryVerification(next, plan), await catalog.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(next, plan)));
    }

    [TestMethod]
    public async Task HistoricalRecoveryVerificationRemainsImmutableAfterRoleRotationWithoutProvingTheLatestAction()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var first = RecoveryAction(conversion, protection); var facts = RecoveryVerification(first, plan);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
            await catalog.PrepareNamespaceBirthRecoveryActionAsync(first); await catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts);
            var rotation = protection with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                RoleSid = "S-1-5-5-987-654",
                ExpectedDacl = protection.TargetDacl,
                TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
                PreparedAt = facts.Verification.ObservedAt.AddSeconds(1),
            };
            await ProtectAsync(catalog, parent, rotation);
            var next = first with
            {
                RecoveryId = Guid.NewGuid(),
                PreviousRecoveryId = first.RecoveryId,
                ParentPermissionOperationId = rotation.OperationId,
                RoleSid = rotation.RoleSid,
                ExpectedDacl = first.ConvertedDacl,
                RoleOnlyDacl = first.RoleOnlyDacl.Replace(Role, rotation.RoleSid, StringComparison.Ordinal),
                ConvertedDacl = first.ConvertedDacl.Replace(Role, rotation.RoleSid, StringComparison.Ordinal),
                PreparedAt = rotation.PreparedAt.AddSeconds(3),
            };
            await catalog.PrepareNamespaceBirthRecoveryActionAsync(next);
            Assert.AreEqual(facts, await catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(
                facts with { Verification = facts.Verification with { ObservedAt = facts.Verification.ObservedAt.AddTicks(1) } }));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var entry = (await second.ReadNamespaceBirthRecoveryPageAsync(await second.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.AreNotEqual(first.RecoveryId, entry.LatestRecoveryAction!.RecoveryId);
        Assert.IsNull(entry.LatestRecoveryVerification);
        Assert.AreEqual(facts, await second.ReadNamespaceBirthRecoveryVerificationAsync(first.RecoveryId));
        Assert.AreEqual(facts, await second.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        var current = RecoveryVerification(entry.LatestRecoveryAction, plan);
        Assert.AreEqual(current, await second.RecordNamespaceBirthRecoveryVerificationAsync(current));
        Assert.AreEqual(conversion, await second.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task ParentRotationBetweenRecoveryIntentAndVerificationRejectsStaleCompletion()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-987-654",
            TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
            ExpectedDacl = protection.TargetDacl,
            PreparedAt = action.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, parent, rotation);
        var facts = RecoveryVerification(action, plan) with { Verification = RecoveryVerification(action, plan).Verification with { ObservedAt = rotation.PreparedAt.AddSeconds(3) } };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
    }

    [TestMethod]
    public async Task BornDirectParentEpochFencesNewRecoveryFactsWhileRetainingHistoricalVerification()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        var parent = await AddBornAsync(catalog, Birth(original, protection) with { IsDirectory = true, RelativePath = "Docs/New" });
        var birth = NestedBirth(parent, "staged.bin", false);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        await catalog.PrepareNamespaceBirthAsync(birth); await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await catalog.RecordNamespaceBirthStartAsync(start); await catalog.PrepareNamespaceBirthConversionAsync(conversion);
        var action = RecoveryAction(conversion, protection) with { ParentPermissionOperationId = Guid.Empty, BornParent = birth.BornParent };
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        var newerParent = parent.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = parent.Protection.ProtectionId,
            Verification = parent.Protection.Verification with { ObservedAt = action.PreparedAt.AddSeconds(1) },
        };
        await catalog.RecordNamespaceBirthProtectionAsync(newerParent);
        var stale = RecoveryVerification(action, plan) with
        { Verification = RecoveryVerification(action, plan).Verification with { ObservedAt = newerParent.Verification.ObservedAt.AddSeconds(1) } };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(stale));
        var next = action with
        {
            RecoveryId = Guid.NewGuid(),
            PreviousRecoveryId = action.RecoveryId,
            BornParent = new(parent.Birth.OperationId, newerParent.ProtectionId),
            PreparedAt = newerParent.Verification.ObservedAt.AddSeconds(2),
        };
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(next);
        var facts = RecoveryVerification(next, plan);
        Assert.AreEqual(facts, await catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        await catalog.RecordNamespaceBirthProtectionAsync(newerParent with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = newerParent.ProtectionId,
            Verification = newerParent.Verification with { ObservedAt = facts.Verification.ObservedAt.AddSeconds(1) },
        });
        Assert.AreEqual(facts, await catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(parent.Birth.OperationId));
    }

    [TestMethod]
    public async Task ActiveCaptureBetweenRecoveryIntentAndVerificationKeepsTheCompletionPending()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-987-654",
            TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
            ExpectedDacl = protection.TargetDacl,
            PreparedAt = action.PreparedAt.AddSeconds(1),
        };
        await catalog.CreateNamespacePermissionTreeAsync(new(Guid.NewGuid(), new(parent, rotation), 1, action.PreparedAt));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(action, plan)));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
    }

    [TestMethod]
    public async Task VerificationCannotReleaseOrRecreateAMissingOriginalNameReservation()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            await first.PrepareNamespaceBirthRecoveryActionAsync(action);
        }
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(action, plan)));
        Assert.IsNull(await second.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.IsFalse((await second.ReadNamespaceBirthRecoveryPageAsync(await second.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single().OwnsNameReservation);
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("birth-index")]
    [DataRow("canonical")]
    [DataRow("binding")]
    public async Task CorruptRecoveryVerificationCannotMasqueradeAsCompletedWork(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection); var facts = RecoveryVerification(action, plan);
        var other = Birth(parent, protection) with { RelativePath = "Docs/other.txt" };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
            await catalog.PrepareNamespaceBirthRecoveryActionAsync(action); await catalog.RecordNamespaceBirthRecoveryVerificationAsync(facts);
            var otherPlan = Plan(other); var otherStart = Start(otherPlan);
            await catalog.PrepareNamespaceBirthAsync(other); await catalog.PrepareNamespaceBirthPlanAsync(otherPlan);
            await catalog.RecordNamespaceBirthStartAsync(otherStart); await catalog.PrepareNamespaceBirthConversionAsync(Conversion(other, otherStart));
        }
        if (scenario == "fingerprint") await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_birth_recovery_verifications SET fingerprint=zeroblob(32);");
        else if (scenario == "birth-index") await ExecuteSqlAsync(fixture.Paths,
            "PRAGMA foreign_keys=ON; UPDATE namespace_birth_recovery_verifications SET birth_operation_id='" + other.OperationId.ToString("D") + "';");
        else if (scenario == "binding") await ReplacePayloadValueAsync(fixture.Paths, "namespace_birth_recovery_verifications", "recovery_id",
            action.RecoveryId, facts.Verification.LocalObject.LocalFileId.ToString("D"), Guid.NewGuid().ToString("D"));
        else
        {
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var query = connection.CreateCommand(); query.CommandText = "SELECT payload FROM namespace_birth_recovery_verifications;";
            byte[] payload = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString((byte[])(await query.ExecuteScalarAsync())!));
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_birth_recovery_verifications SET payload=$payload,fingerprint=$fingerprint;";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await update.ExecuteNonQueryAsync();
        }
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.RecordNamespaceBirthRecoveryVerificationAsync(facts));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespaceBirthRecoveryPageAsync(new(1), 0, 1));
    }

    [TestMethod]
    public async Task Schema33MigrationPreservesOriginalRecoveryBytesWithoutInventingVerification()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            await first.PrepareNamespaceBirthRecoveryActionAsync(action);
        }
        string originalBytes = await ReadRecoveryMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_recovery_verifications; PRAGMA user_version=33;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(originalBytes, await ReadRecoveryMigrationBytesAsync(fixture.Paths));
        Assert.AreEqual(action, await current.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.IsNull(await current.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        var entry = (await current.ReadNamespaceBirthRecoveryPageAsync(await current.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.IsNull(entry.LatestRecoveryVerification); Assert.IsNull(entry.Observation); Assert.IsTrue(entry.OwnsNameReservation);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(56L, await query.ExecuteScalarAsync());
        Assert.AreEqual(RecoveryVerification(action, plan), await current.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(action, plan)));
    }

    [TestMethod]
    public async Task CanceledVerificationKeepsTheOriginalActionWithoutClaimingCompletion()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.RecordNamespaceBirthRecoveryVerificationAsync(RecoveryVerification(action, plan), canceled.Token));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.AreEqual(action, await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
    }

    private static MirrorPulseNamespaceBirthRecoveryVerification RecoveryVerification(MirrorPulseNamespaceBirthRecoveryAction action,
        MirrorPulseNamespaceBirthPlan plan) => new(1, action.RecoveryId, action.BirthOperationId,
            new(action.LocalObject, action.OwnerSid, action.ConvertedDacl, action.PreparedAt.AddSeconds(1)),
            plan.ItemId, plan.RemoteId, plan.RemoteRevision, true, false, 1);

    private static async Task<string> ReadRecoveryMigrationBytesAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT hex(c.payload)||':'||hex(c.fingerprint)||':'||hex(a.payload)||':'||hex(a.fingerprint)
            FROM namespace_birth_conversions c JOIN namespace_birth_recovery_actions a ON a.birth_operation_id=c.operation_id;
            """;
        return (string)(await query.ExecuteScalarAsync())!;
    }
}
