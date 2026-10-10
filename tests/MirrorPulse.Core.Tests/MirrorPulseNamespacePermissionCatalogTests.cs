using System.Security.AccessControl;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseNamespacePermissionCatalogTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string FirstRole = "S-1-5-5-123-456";
    private const string SecondRole = "S-1-5-5-123-457";
    private static readonly string Original = Dacl($"D:AI(A;OICI;FA;;;{Owner})");
    private static readonly string FirstTarget = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{FirstRole})");
    private static readonly string SecondTarget = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{SecondRole})");

    [TestMethod]
    public async Task NativeInheritanceMarkerRetainsActualVerificationAndOriginalRestorationAcrossRestart()
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline() with { OriginalDacl = Dacl($"D:P(A;OICI;FA;;;{Owner})") };
        var first = Intent(baseline) with { ExpectedDacl = baseline.OriginalDacl };
        string firstObserved = WithAutoInherited(first.TargetDacl);
        var restore = first with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = firstObserved,
            TargetDacl = baseline.OriginalDacl,
            PreparedAt = first.PreparedAt.AddSeconds(3),
        };
        string restoredObserved = WithAutoInherited(baseline.OriginalDacl);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, first);
            await catalog.RecordNamespacePermissionApplicationAsync(first.OperationId, baseline.LocalObject, first.PreparedAt);
            await catalog.VerifyNamespacePermissionChangeAsync(first.OperationId, new(baseline.LocalObject, Owner, firstObserved, first.PreparedAt.AddSeconds(1)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline,
                restore with { ExpectedDacl = first.TargetDacl }));
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, restore);
            await catalog.RecordNamespacePermissionApplicationAsync(restore.OperationId, baseline.LocalObject, restore.PreparedAt);
            await catalog.VerifyNamespacePermissionChangeAsync(restore.OperationId,
                new(baseline.LocalObject, Owner, restoredObserved, restore.PreparedAt.AddSeconds(1)));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var retained = (await reopened.ReadNamespacePermissionChangeAsync(restore.OperationId))!;
            Assert.AreEqual(baseline.OriginalDacl, retained.Intent.TargetDacl);
            Assert.AreEqual(restoredObserved, retained.Verification!.Dacl);
            Assert.AreNotEqual(retained.Intent.TargetDacl, retained.Verification.Dacl);
            Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            await reopened.PrepareNamespacePermissionChangeAsync(baseline, first with
            {
                OperationId = Guid.NewGuid(),
                ExpectedDacl = restoredObserved,
                PreparedAt = restore.PreparedAt.AddSeconds(3),
            });
            Assert.HasCount(3, await reopened.ReadNamespacePermissionChangesAsync());
        }
    }

    [TestMethod]
    [DataRow("remove-completion")]
    [DataRow("remove-protection")]
    [DataRow("add-inherit-request")]
    [DataRow("change-rights")]
    [DataRow("change-role")]
    public async Task NativeReadbackNeverAcceptsDifferentRightsOrOtherDescriptorFlags(string scenario)
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        var intent = Intent(baseline) with { TargetDacl = WithAutoInherited(FirstTarget) };
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt);
        var descriptor = new RawSecurityDescriptor(intent.TargetDacl);
        string changed;
        if (scenario == "change-rights") changed = Dacl($"D:PAI(A;OICI;FA;;;{Owner})(A;OICI;FA;;;{FirstRole})");
        else if (scenario == "change-role") changed = WithAutoInherited(SecondTarget);
        else
        {
            ControlFlags flags = scenario switch
            {
                "remove-completion" => descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited,
                "remove-protection" => descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclProtected,
                _ => descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired,
            };
            descriptor.SetFlags(flags);
            changed = descriptor.GetSddlForm(AccessControlSections.Access);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId,
            new(baseline.LocalObject, Owner, changed, intent.PreparedAt)));
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, (await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId))!.Phase);
        Assert.AreEqual(baseline, await catalog.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
    }

    [TestMethod]
    public async Task PreparationBatchSurvivesRestartWithoutResettingAnAppliedMember()
    {
        using var fixture = new CatalogFixture();
        var first = Baseline();
        var second = Baseline() with
        {
            RootId = first.RootId,
            RelativePath = "Docs/Nested/second.txt",
            LocalObject = first.LocalObject with { LocalFileId = Guid.NewGuid() },
        };
        MirrorPulseNamespacePermissionPreparation[] batch = [new(first, Intent(first)), new(second, Intent(second))];
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            IReadOnlyList<MirrorPulseNamespacePermissionChange> changes = await catalog.PrepareNamespacePermissionChangesAsync(batch);
            Assert.HasCount(2, changes);
            CollectionAssert.AreEqual(batch.Select(item => item.Intent).ToArray(), changes.Select(item => item.Intent).ToArray());
            Assert.IsTrue(changes.All(change => change.Phase == MirrorPulseNamespacePermissionPhase.Prepared && change.AppliedAt is null));
            await catalog.RecordNamespacePermissionApplicationAsync(batch[0].Intent.OperationId, first.LocalObject, batch[0].Intent.PreparedAt);
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            IReadOnlyList<MirrorPulseNamespacePermissionChange> replay = await reopened.PrepareNamespacePermissionChangesAsync(batch);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, replay[0].Phase);
            Assert.AreEqual(batch[0].Intent.PreparedAt, replay[0].AppliedAt);
            Assert.IsNull(replay[0].Verification);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, replay[1].Phase);
            Assert.AreEqual(first, await reopened.ReadNamespacePermissionBaselineAsync(first.EvidenceId));
            Assert.AreEqual(second, await reopened.ReadNamespacePermissionBaselineAsync(second.EvidenceId));
            Assert.HasCount(2, await reopened.ReadNamespacePermissionChangesAsync());
        }
    }

    [TestMethod]
    public async Task LaterObjectConflictRollsBackEveryNewBatchRecordAndPreservesEarlierHistory()
    {
        using var fixture = new CatalogFixture();
        var retained = Baseline();
        var prior = Intent(retained);
        var fresh = Baseline();
        var freshIntent = Intent(fresh);
        var alias = retained with { EvidenceId = Guid.NewGuid() };
        var aliasIntent = Intent(alias);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(retained, prior);
            // The first insert succeeds inside the transaction; the later native-object
            // uniqueness conflict must roll it back rather than leave partial originals.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangesAsync(
                [new(fresh, freshIntent), new(alias, aliasIntent)]));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(fresh.EvidenceId));
            Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(alias.EvidenceId));
            Assert.IsNull(await reopened.ReadNamespacePermissionChangeAsync(freshIntent.OperationId));
            Assert.IsNull(await reopened.ReadNamespacePermissionChangeAsync(aliasIntent.OperationId));
            Assert.AreEqual(retained, await reopened.ReadNamespacePermissionBaselineAsync(retained.EvidenceId));
            Assert.AreEqual(prior, (await reopened.ReadNamespacePermissionChangeAsync(prior.OperationId))!.Intent);
            Assert.HasCount(1, await reopened.ReadNamespacePermissionChangesAsync());
        }
    }

    [TestMethod]
    [DataRow("object")]
    [DataRow("operation")]
    [DataRow("empty")]
    [DataRow("oversized")]
    [DataRow("misbound")]
    public async Task InvalidPreparationBatchLeavesNoOriginals(string scenario)
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = Baseline();
        var second = scenario == "object" ? first with { EvidenceId = Guid.NewGuid() } : Baseline();
        var firstIntent = Intent(first);
        var secondIntent = Intent(second);
        if (scenario == "operation") secondIntent = secondIntent with { OperationId = firstIntent.OperationId };
        if (scenario == "misbound") secondIntent = secondIntent with { EvidenceId = Guid.NewGuid() };
        MirrorPulseNamespacePermissionPreparation[] batch = scenario switch
        {
            "empty" => [],
            "oversized" => Enumerable.Repeat(new MirrorPulseNamespacePermissionPreparation(first, firstIntent), 4097).ToArray(),
            _ => [new(first, firstIntent), new(second, secondIntent)],
        };
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangesAsync(batch));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(first.EvidenceId));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(second.EvidenceId));
        Assert.IsEmpty(await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task CancelledPreparationBatchRetainsNoNewEvidence()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareNamespacePermissionChangesAsync(
            [new(baseline, Intent(baseline))], cancelled.Token));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
        Assert.IsEmpty(await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task OriginalAclBindingAndSeparateApplicationVerificationSurviveOwnersAndRoleRotation()
    {
        using var fixture = new CatalogFixture();
        MirrorPulseNamespacePermissionBaseline baseline = Baseline();
        MirrorPulseNamespacePermissionIntent first = Intent(baseline);
        DateTimeOffset applied = first.PreparedAt.AddSeconds(1);
        var verification = new MirrorPulseNamespacePermissionVerification(baseline.LocalObject, Owner, FirstTarget, applied.AddSeconds(1));
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await catalog.PrepareNamespacePermissionChangeAsync(baseline, first);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, prepared.Phase);
            Assert.IsNull(prepared.Verification);
            Assert.AreEqual(prepared, await catalog.PrepareNamespacePermissionChangeAsync(baseline, first));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(first.OperationId, verification));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await reopened.ReadNamespacePermissionChangeAsync(first.OperationId))!.Phase);
            var application = await reopened.RecordNamespacePermissionApplicationAsync(first.OperationId, baseline.LocalObject, applied);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, application.Phase);
            Assert.IsNull(application.Verification);
            Assert.AreEqual(application, await reopened.RecordNamespacePermissionApplicationAsync(first.OperationId, baseline.LocalObject, applied));
        }
        MirrorPulseNamespacePermissionIntent rotation = first with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = SecondRole,
            ExpectedDacl = FirstTarget,
            TargetDacl = SecondTarget,
            RelativePath = "Renamed/Nested/edited.txt",
            PreparedAt = applied.AddSeconds(3),
        };
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, (await reopened.ReadNamespacePermissionChangeAsync(first.OperationId))!.Phase);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespacePermissionChangeAsync(baseline, rotation));
            var verified = await reopened.VerifyNamespacePermissionChangeAsync(first.OperationId, verification);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, verified.Phase);
            Assert.AreEqual(verified, await reopened.VerifyNamespacePermissionChangeAsync(first.OperationId, verification));
            await reopened.PrepareNamespacePermissionChangeAsync(baseline, rotation);
            await reopened.RecordNamespacePermissionApplicationAsync(rotation.OperationId, baseline.LocalObject, rotation.PreparedAt.AddSeconds(1));
            await reopened.VerifyNamespacePermissionChangeAsync(rotation.OperationId, new(baseline.LocalObject, Owner, SecondTarget, rotation.PreparedAt.AddSeconds(2)));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var history = await reopened.ReadNamespacePermissionChangesAsync();
            Assert.HasCount(2, history);
            Assert.AreEqual(first, history[0].Intent);
            Assert.AreEqual(verification, history[0].Verification);
            Assert.AreEqual(rotation, history[1].Intent);
            Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            Assert.AreEqual("Docs/Nested/edited.txt", baseline.RelativePath);
        }
    }

    [TestMethod]
    public async Task ObjectSwapWrongOwnerAndChangedAclNeverBecomeVerified()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        var intent = Intent(baseline);
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        var wrongFile = baseline.LocalObject with { LocalFileId = Guid.NewGuid() };
        var wrongVolume = baseline.LocalObject with { VolumeSerialNumber = 2 };
        var wrongRoot = baseline.LocalObject with { SyncRootFileId = Guid.NewGuid() };
        foreach (var wrong in new[] { wrongFile, wrongVolume, wrongRoot })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, wrong, intent.PreparedAt));
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt);
        var correct = new MirrorPulseNamespacePermissionVerification(baseline.LocalObject, Owner, FirstTarget, intent.PreparedAt);
        foreach (var wrong in new[] { wrongFile, wrongVolume, wrongRoot })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, correct with { LocalObject = wrong }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId,
            correct with { OwnerSid = "S-1-5-21-100-200-300-1002" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, correct with { Dacl = Original }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, correct with { ObservedAt = intent.PreparedAt.AddTicks(-1) }));
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, (await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId))!.Phase);
        await catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, correct);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, correct with { ObservedAt = correct.ObservedAt.AddSeconds(1) }));
    }

    [TestMethod]
    public async Task BaselineOperationAndApplicationFactsAreImmutableAndRejectedPreparationLeavesNoOrphan()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        var intent = Intent(baseline);
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        foreach (var changed in new[]
        {
            baseline with { OriginalDacl = FirstTarget }, baseline with { OwnerSid = FirstRole },
            baseline with { CapturedAt = baseline.CapturedAt.AddTicks(1) }, baseline with { RelativePath = "Other/edited.txt" },
        })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(changed, intent));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangeAsync(
            baseline with { LocalObject = baseline.LocalObject with { LocalFileId = Guid.NewGuid() } }, intent));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline, intent with { TargetDacl = SecondTarget }));
        var alias = baseline with { EvidenceId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(alias,
            intent with { EvidenceId = alias.EvidenceId, OperationId = Guid.NewGuid() }));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(alias.EvidenceId));
        var unrelated = Baseline();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(unrelated,
            Intent(unrelated) with { OperationId = intent.OperationId }));
        Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(unrelated.EvidenceId));
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId,
            baseline.LocalObject, intent.PreparedAt.AddSeconds(1)));
        Assert.HasCount(1, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task RecoveryRetainsOriginalAndAppliedFactsAndFencesFurtherWritesAcrossRestart()
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline();
        var intent = Intent(baseline);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt);
            var recovery = await catalog.RequireNamespacePermissionRecoveryAsync(intent.OperationId, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged);
            Assert.AreEqual(intent.PreparedAt, recovery.AppliedAt);
            Assert.IsNull(recovery.Verification);
            Assert.AreEqual(recovery, await catalog.RequireNamespacePermissionRecoveryAsync(intent.OperationId, MirrorPulseNamespacePermissionRecoveryReason.DaclChanged));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var recovery = await reopened.ReadNamespacePermissionChangeAsync(intent.OperationId);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.RecoveryRequired, recovery!.Phase);
            Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.VerifyNamespacePermissionChangeAsync(intent.OperationId,
                new(baseline.LocalObject, Owner, FirstTarget, intent.PreparedAt)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RequireNamespacePermissionRecoveryAsync(intent.OperationId, MirrorPulseNamespacePermissionRecoveryReason.Interrupted));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespacePermissionChangeAsync(baseline,
                intent with { OperationId = Guid.NewGuid() }));
        }
    }

    [TestMethod]
    public async Task RestoreAndReprotectAlwaysUseFirstBaselineAndVerifiedDaclChain()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        var first = Intent(baseline);
        await CompleteAsync(catalog, baseline, first);
        var restore = first with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            ExpectedDacl = FirstTarget,
            TargetDacl = Original,
            PreparedAt = first.PreparedAt.AddSeconds(3),
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline, restore with { TargetDacl = SecondTarget }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline, restore with { RoleSid = SecondRole }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline,
            first with { OperationId = Guid.NewGuid(), PreparedAt = restore.PreparedAt }));
        await CompleteAsync(catalog, baseline, restore);
        var reprotect = first with { OperationId = Guid.NewGuid(), RoleSid = SecondRole, TargetDacl = SecondTarget, PreparedAt = restore.PreparedAt.AddSeconds(3) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline,
            reprotect with { Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole }));
        await CompleteAsync(catalog, baseline, reprotect);
        Assert.HasCount(3, await catalog.ReadNamespacePermissionChangesAsync());
        Assert.AreEqual(baseline, await catalog.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
    }

    [TestMethod]
    public async Task IndependentObjectsHaveIndependentPendingChangesAndSyncRootHasItsOwnBinding()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var file = Baseline();
        Guid syncId = file.LocalObject.SyncRootFileId;
        var sync = file with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            IsDirectory = true,
            LocalObject = file.LocalObject with { LocalFileId = syncId }
        };
        await catalog.PrepareNamespacePermissionChangeAsync(file, Intent(file));
        await catalog.PrepareNamespacePermissionChangeAsync(sync, Intent(sync));
        Assert.HasCount(2, await catalog.ReadNamespacePermissionChangesAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(file, Intent(file)));
    }

    [TestMethod]
    public async Task Schema21UpgradePreservesWorkerEvidenceAndDoesNotInventOriginalAcl()
    {
        using var fixture = new CatalogFixture();
        Guid operationId = Guid.NewGuid();
        var instanceId = InstanceId.New();
        byte[] fingerprint = Enumerable.Repeat((byte)42, 32).ToArray();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.TryRecordWorkerRequestAsync(operationId, instanceId, fingerprint);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_permission_changes; DROP TABLE namespace_permission_baselines; PRAGMA user_version=21;");
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.IsFalse(await reopened.TryRecordWorkerRequestAsync(operationId, instanceId, fingerprint));
            Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
            Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(Guid.NewGuid()));
            var baseline = Baseline();
            await reopened.PrepareNamespacePermissionChangeAsync(baseline, Intent(baseline));
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(27L, await command.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("UPDATE namespace_permission_changes SET phase=1;")]
    [DataRow("UPDATE namespace_permission_baselines SET volume_serial='2';")]
    [DataRow("UPDATE namespace_permission_changes SET payload=json_set(payload, '$.Intent.LocalObject.VolumeSerialNumber', 2);")]
    public async Task CorruptRetainedStateIsRejectedOnReopen(string corruption)
    {
        using var fixture = new CatalogFixture();
        var baseline = Baseline();
        var intent = Intent(baseline);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await ExecuteSqlAsync(fixture.Paths, corruption);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionChangeAsync(intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    [DataRow("../other.txt")]
    [DataRow("Docs/../other.txt")]
    [DataRow("Docs\\edited.txt")]
    [DataRow("C:/outside.txt")]
    [DataRow("/Docs/edited.txt")]
    [DataRow("Docs/edited.txt:stream")]
    [DataRow("Docs/edited.txt.")]
    [DataRow("Docs//edited.txt")]
    public async Task NonCanonicalOrEscapingCaptureLocationIsRejectedBeforePersistence(string path)
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline() with { RelativePath = path };
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline, Intent(baseline)));
        Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task MalformedEvidenceAndNonLogonRoleAreRejectedBeforePersistence()
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var valid = Baseline();
        foreach (var bad in new[]
        {
            valid with { EvidenceId = Guid.Empty }, valid with { LocalObject = valid.LocalObject with { VolumeSerialNumber = 0 } },
            valid with { RootId = default(RootId) }, valid with { RootId = null }, valid with { OriginalDacl = string.Empty },
            valid with { OriginalDacl = "O:SYD:(A;;FA;;;SY)" }, valid with { OriginalDacl = "D:(A;;FA;;;SY)S:(AU;SA;FA;;;WD)" },
            valid with { OriginalDacl = new string('X', 65_537) }, valid with { CapturedAt = default },
        })
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangeAsync(bad, Intent(bad)));
        foreach (string role in new[] { Owner, "S-1-5-18", string.Empty })
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangeAsync(valid, Intent(valid) with { RoleSid = role }));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespacePermissionChangeAsync(valid, Intent(valid) with { TargetDacl = Original }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(valid,
            Intent(valid) with { RelativePath = "Other/edited.txt" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(valid,
            Intent(valid) with { Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole }));
        Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
    }

    private static MirrorPulseNamespacePermissionBaseline Baseline() => new(Guid.NewGuid(), RootId.New(),
        new(1, Guid.NewGuid(), Guid.NewGuid()), "Docs/Nested/edited.txt", false, Owner, Original, DateTimeOffset.UtcNow);

    private static MirrorPulseNamespacePermissionIntent Intent(MirrorPulseNamespacePermissionBaseline baseline) => new(Guid.NewGuid(),
        baseline.EvidenceId, baseline.LocalObject, baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect,
        FirstRole, Original, FirstTarget, baseline.CapturedAt.AddSeconds(1));

    private static async Task CompleteAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespacePermissionBaseline baseline,
        MirrorPulseNamespacePermissionIntent intent)
    {
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt.AddSeconds(1));
        await catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, new(baseline.LocalObject, Owner, intent.TargetDacl, intent.PreparedAt.AddSeconds(2)));
    }

    private static string Dacl(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);

    private static string WithAutoInherited(string value)
    {
        var descriptor = new RawSecurityDescriptor(value);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-permission-catalog", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public CatalogFixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
