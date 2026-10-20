using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task RecoveryIntentSurvivesOwnersWithoutClaimingThatAnyNativeWriteOccurred()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            Assert.AreEqual(action, await first.PrepareNamespaceBirthRecoveryActionAsync(action));
            Assert.AreEqual(action, await first.PrepareNamespaceBirthRecoveryActionAsync(action));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(action, await second.ReadNamespaceBirthRecoveryActionAsync(action.RecoveryId));
        Assert.AreEqual(action, await second.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        var entry = (await second.ReadNamespaceBirthRecoveryPageAsync(await second.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.AreEqual(action, entry.LatestRecoveryAction);
        Assert.AreEqual(conversion, entry.ConversionPreparation);
        Assert.AreEqual(birth, entry.Intent);
        Assert.IsNull(entry.Observation); Assert.IsNull(entry.LatestProtection);
        Assert.IsTrue(entry.OwnsNameReservation);
        Assert.IsNull(await second.ReadNamespacePermissionBaselineAsync(birth.OperationId));
        Assert.IsNull(await second.ReadMutationAsync(birth.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("time")]
    [DataRow("expected-dacl")]
    [DataRow("parent")]
    [DataRow("role")]
    [DataRow("missing-binding")]
    [DataRow("directory")]
    public async Task RecoveryCannotAdoptAnUnrelatedStagedObjectOrParent(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        if (scenario == "directory") birth = birth with { IsDirectory = true };
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        var changed = scenario switch
        {
            "binding" => action with { LocalObject = action.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "owner" => action with { OwnerSid = "S-1-5-21-100-200-300-9999" },
            "time" => action with { PreparedAt = conversion.PreparedAt.AddTicks(-1) },
            "expected-dacl" => action with { ExpectedDacl = Original },
            "parent" => action with { ParentPermissionOperationId = Guid.NewGuid() },
            "role" => action with { RoleSid = "S-1-5-5-987-654" },
            _ => action,
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        if (scenario != "missing-binding") await catalog.PrepareNamespaceBirthConversionAsync(conversion);
        if (scenario is "parent" or "missing-binding")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(changed));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(changed));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow("expected")]
    [DataRow("role-only")]
    [DataRow("converted")]
    public async Task SubsequentRecoveryRetainsEachOwnedCrashStateAndImmutablePredecessor(string state)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var first = RecoveryAction(conversion, protection);
        var next = first with
        {
            RecoveryId = Guid.NewGuid(),
            PreviousRecoveryId = first.RecoveryId,
            PreparedAt = first.PreparedAt.AddSeconds(1),
            ExpectedDacl = state switch { "expected" => first.ExpectedDacl, "role-only" => first.RoleOnlyDacl, _ => first.ConvertedDacl },
        };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
            await catalog.PrepareNamespaceBirthRecoveryActionAsync(first);
            Assert.AreEqual(next, await catalog.PrepareNamespaceBirthRecoveryActionAsync(next));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(next with { ConvertedDacl = Original }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(next with { RecoveryId = Guid.NewGuid() }));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(first, await second.ReadNamespaceBirthRecoveryActionAsync(first.RecoveryId));
        Assert.AreEqual(next, await second.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.AreEqual(conversion, await second.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task RecoveryUsesCurrentParentRoleWhileKeepingHistoricalAdmissionAndActionReplay()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var first = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(first);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-987-654",
            ExpectedDacl = protection.TargetDacl,
            TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
            PreparedAt = first.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, parent, rotation);
        Assert.AreEqual(first, await catalog.PrepareNamespaceBirthRecoveryActionAsync(first));
        var next = first with { RecoveryId = Guid.NewGuid(), PreviousRecoveryId = first.RecoveryId, PreparedAt = rotation.PreparedAt.AddSeconds(2) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(next));
        next = next with
        {
            ParentPermissionOperationId = rotation.OperationId,
            RoleSid = rotation.RoleSid,
            RoleOnlyDacl = first.RoleOnlyDacl.Replace(Role, rotation.RoleSid, StringComparison.Ordinal),
            ConvertedDacl = first.ConvertedDacl.Replace(Role, rotation.RoleSid, StringComparison.Ordinal),
        };
        Assert.AreEqual(next, await catalog.PrepareNamespaceBirthRecoveryActionAsync(next));
        Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(conversion, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task BornParentRecoveryRequiresItsCurrentDirectEpochWithoutInventingOriginalPermissions()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProtection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, originalProtection);
        var parent = await AddBornAsync(catalog, Birth(original, originalProtection) with { IsDirectory = true, RelativePath = "Docs/New" });
        var birth = NestedBirth(parent, "staged.bin", false);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        await catalog.PrepareNamespaceBirthAsync(birth); await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await catalog.RecordNamespaceBirthStartAsync(start); await catalog.PrepareNamespaceBirthConversionAsync(conversion);
        var action = RecoveryAction(conversion, originalProtection) with
        {
            ParentPermissionOperationId = Guid.Empty,
            BornParent = birth.BornParent,
        };
        Assert.AreEqual(action, await catalog.PrepareNamespaceBirthRecoveryActionAsync(action));
        var newerParent = parent.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = parent.Protection.ProtectionId,
            Verification = parent.Protection.Verification with { ObservedAt = action.PreparedAt.AddSeconds(1) },
        };
        await catalog.RecordNamespaceBirthProtectionAsync(newerParent);
        var next = action with { RecoveryId = Guid.NewGuid(), PreviousRecoveryId = action.RecoveryId, PreparedAt = newerParent.Verification.ObservedAt.AddSeconds(1) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(next));
        next = next with { BornParent = new(parent.Birth.OperationId, newerParent.ProtectionId) };
        Assert.AreEqual(next, await catalog.PrepareNamespaceBirthRecoveryActionAsync(next));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(parent.Birth.OperationId));
        Assert.AreEqual(parent.Observation, await catalog.ReadNamespaceBirthObservationAsync(parent.Birth.OperationId));
    }

    [TestMethod]
    public async Task RecoveryParentClaimsMustNotAllowProtectionToOmitItsBornDirectParent()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProtection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, originalProtection);
        var parent = await AddBornAsync(catalog, Birth(original, originalProtection) with { IsDirectory = true, RelativePath = "Docs/New" });
        var child = await AddBornAsync(catalog, NestedBirth(parent, "child.bin", false));
        var invalid = child.Protection with
        {
            Version = 1,
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = child.Protection.ProtectionId,
            BornParent = null,
            ParentPermissionOperationId = originalProtection.OperationId,
            Verification = child.Protection.Verification with { ObservedAt = child.Protection.Verification.ObservedAt.AddSeconds(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthProtectionAsync(invalid));
        Assert.AreEqual(child.Protection, await catalog.ReadLatestNamespaceBirthProtectionAsync(child.Birth.OperationId));
    }

    [TestMethod]
    public async Task AnObservedBirthCanReplayItsIntentButCannotAdmitAnotherStagedRecovery()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        await catalog.RecordNamespaceBirthObservationAsync(Observation(birth, plan, start) with { LocalObject = conversion.LocalObject });
        Assert.AreEqual(action, await catalog.PrepareNamespaceBirthRecoveryActionAsync(action));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(
            action with { RecoveryId = Guid.NewGuid(), PreviousRecoveryId = action.RecoveryId }));
    }

    [TestMethod]
    public async Task CompetingFirstRecoveryIntentsHaveOneDurableWinner()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var left = RecoveryAction(conversion, protection); var right = left with { RecoveryId = Guid.NewGuid() };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        var results = await Task.WhenAll(Task.Run(() => AttemptAsync(left)), Task.Run(() => AttemptAsync(right)));
        var winner = results.OfType<MirrorPulseNamespaceBirthRecoveryAction>().Single();
        Assert.AreEqual(winner, await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        async Task<MirrorPulseNamespaceBirthRecoveryAction?> AttemptAsync(MirrorPulseNamespaceBirthRecoveryAction action)
        {
            try { return await catalog.PrepareNamespaceBirthRecoveryActionAsync(action); }
            catch (InvalidOperationException) { return null; }
        }
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("sequence")]
    [DataRow("parent-index")]
    public async Task DamagedRecoveryRowsCannotBecomeOwnedRecoveryIntents(string damage)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        Guid alternateParent = Guid.Empty;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            await first.PrepareNamespaceBirthRecoveryActionAsync(action);
            if (damage == "parent-index")
            {
                var otherParent = Baseline(); var otherProtection = Protection(otherParent);
                await ProtectAsync(first, otherParent, otherProtection);
                alternateParent = otherProtection.OperationId;
            }
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => "UPDATE namespace_birth_recovery_actions SET fingerprint=zeroblob(32);",
            "sequence" => "UPDATE namespace_birth_recovery_actions SET sequence=0;",
            _ => "UPDATE namespace_birth_recovery_actions SET parent_permission_operation_id='" + alternateParent.ToString("D") + "';",
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthRecoveryActionAsync(action.RecoveryId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task LatestRecoveryCannotHideAnOlderDamagedDescriptorAdmission()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var first = RecoveryAction(conversion, protection);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
            await catalog.PrepareNamespaceBirthRecoveryActionAsync(first);
            var previous = first;
            for (int index = 0; index < 2; index++)
            {
                previous = previous with
                {
                    RecoveryId = Guid.NewGuid(),
                    PreviousRecoveryId = previous.RecoveryId,
                    PreparedAt = previous.PreparedAt.AddSeconds(1),
                    ExpectedDacl = previous.RoleOnlyDacl,
                };
                await catalog.PrepareNamespaceBirthRecoveryActionAsync(previous);
            }
        }
        await ReplacePayloadValueAsync(fixture.Paths, "namespace_birth_recovery_actions", "recovery_id",
            first.RecoveryId, first.ExpectedDacl, Original);
        await using var next = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => next.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.AreEqual(conversion, await next.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task CanceledRecoveryAdmissionRetainsTheOriginalBindingWithoutAnAction()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(RecoveryAction(conversion, protection), canceled.Token));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.AreEqual(conversion, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task ActiveAncestorCaptureFencesNewRecoveryBeforeAnyActionIsPersisted()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        var rotation = protection with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-987-654",
            ExpectedDacl = protection.TargetDacl,
            TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
            PreparedAt = action.PreparedAt.AddSeconds(1),
        };
        await catalog.CreateNamespacePermissionTreeAsync(new(Guid.NewGuid(), new(parent, rotation), 1, action.PreparedAt));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthRecoveryActionAsync(action));
        Assert.IsNull(await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.AreEqual(conversion, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task MissingOriginalReservationCannotBeRecreatedFromARecoveryIntent()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        await using (var owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await PrepareStagedRecoveryAsync(owner, parent, protection, birth, plan, start, conversion);
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => current.PrepareNamespaceBirthRecoveryActionAsync(RecoveryAction(conversion, protection)));
        Assert.IsNull(await current.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        var entry = (await current.ReadNamespaceBirthRecoveryPageAsync(await current.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.IsFalse(entry.OwnsNameReservation);
        Assert.AreEqual(conversion, entry.ConversionPreparation);
    }

    [TestMethod]
    public async Task Schema32AddsAnEmptyRecoveryLedgerAndPreservesOriginalNativeFacts()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        await using (var old = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await PrepareStagedRecoveryAsync(old, parent, protection, birth, plan, start, conversion);
        string originalBytes;
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_birth_conversions;";
            originalBytes = (string)(await query.ExecuteScalarAsync())!;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_recovery_actions; PRAGMA user_version=32;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await current.ReadLatestNamespaceBirthRecoveryActionAsync(birth.OperationId));
        Assert.AreEqual(birth, await current.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(conversion, await current.ReadNamespaceBirthConversionAsync(birth.OperationId));
        Assert.IsNull(await current.ReadNamespaceBirthObservationAsync(birth.OperationId));
        var action = RecoveryAction(conversion, protection);
        Assert.AreEqual(action, await current.PrepareNamespaceBirthRecoveryActionAsync(action));
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_birth_conversions;";
            Assert.AreEqual(originalBytes, await query.ExecuteScalarAsync());
            query.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(79L, await query.ExecuteScalarAsync());
        }
    }

    private static MirrorPulseNamespaceBirthRecoveryAction RecoveryAction(MirrorPulseNamespaceBirthConversionPreparation conversion,
        MirrorPulseNamespacePermissionIntent parent) => new(1, Guid.NewGuid(), conversion.OperationId, null,
            conversion.LocalObject, conversion.OwnerSid, parent.RoleSid, parent.OperationId, conversion.BirthDacl,
            Dacl($"D:P(A;;0x00160080;;;{Owner})(A;;FA;;;{parent.RoleSid})"), Protected.Replace(Role, parent.RoleSid, StringComparison.Ordinal),
            conversion.PreparedAt.AddSeconds(1));

    private static async Task PrepareStagedRecoveryAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespacePermissionBaseline parent,
        MirrorPulseNamespacePermissionIntent protection, MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthPlan plan,
        MirrorPulseNamespaceBirthStart start, MirrorPulseNamespaceBirthConversionPreparation conversion)
    {
        await PrepareStartedBirthAsync(catalog, parent, protection, birth, plan, start);
        await catalog.PrepareNamespaceBirthConversionAsync(conversion);
    }
}
