using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task PreservingDeleteOriginalAndCallerSuppliedConflictReceiptRemainImmutableAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseRemoteDeleteRejectionAdmission original; MirrorPulseRemoteDeleteRejectionOutcome outcome;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await RejectionAdmissionAsync(first);
            var prepared = await first.PrepareRemoteDeleteRejectionAsync(original);
            Assert.AreEqual(original, prepared.Admission); Assert.IsNull(prepared.Outcome);
            Assert.AreEqual(prepared, await first.PrepareRemoteDeleteRejectionAsync(original));
            Assert.AreEqual(prepared, (await first.ReadPendingRemoteDeleteRejectionsAsync(original.RootId)).Single());
            Assert.Contains(original.RootId, await first.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsTrue(await first.CanResumeRemoteDeleteRejectionAsync(original));
            Assert.IsFalse(await first.CanResumeRemoteDeleteRejectionAsync(original with { EntryFingerprint = new('C', 64) }));
            Assert.AreEqual(new MirrorPulseNamespacePendingOperation(original.OperationId, original.RootId, MirrorPulseNamespacePendingOperationKind.RemoteDeleteRejection),
                (await first.ReadNamespacePendingOperationsAsync(original.RootId)).Single());
            Assert.IsEmpty(await first.ReadPendingRemoteDeleteRejectionsAsync(RootId.New()));
            outcome = new(original.OperationId, Guid.NewGuid(), original.StartedAt.AddSeconds(1), original.StartedAt.AddSeconds(2));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(new(original, null), await second.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal));
            var completed = await second.CompleteRemoteDeleteRejectionAsync(original, outcome);
            Assert.AreEqual(new(original, outcome), completed);
            Assert.AreEqual(completed, await second.CompleteRemoteDeleteRejectionAsync(original, outcome));
            Assert.IsEmpty(await second.ReadPendingRemoteDeleteRejectionsAsync(original.RootId));
            Assert.IsFalse((await second.ReadNamespaceDispatchFencedRootsAsync()).Contains(original.RootId));
            Assert.IsFalse(await second.CanResumeRemoteDeleteRejectionAsync(original));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.CompleteRemoteDeleteRejectionAsync(original,
                outcome with { ConflictId = Guid.NewGuid() }));
            Assert.IsNull(await second.ReadNamespaceRemoteRetirementAsync(original.InstanceId, original.BatchId, original.Ordinal));
        }
        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(new(original, outcome), await third.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("entry")]
    [DataRow("item")]
    [DataRow("operation")]
    [DataRow("time")]
    [DataRow("policy")]
    [DataRow("change")]
    public async Task PreservingDeleteReplayCannotAdoptAnotherOriginalEntry(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await RejectionAdmissionAsync(catalog); var prepared = await catalog.PrepareRemoteDeleteRejectionAsync(original);
        var substitute = changed switch
        {
            "entry" => original with { EntryFingerprint = new('C', 64) },
            "item" => original with { ItemId = Guid.NewGuid() },
            "operation" => original with { OperationId = Guid.NewGuid() },
            "time" => original with { StartedAt = original.StartedAt.AddTicks(1) },
            "policy" => original with { Policy = original.Policy with { EchoSuppressionObservationCount = 3 } },
            _ => original with { ChangeId = "another-change" }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRemoteDeleteRejectionAsync(substitute));
        Assert.AreEqual(prepared, await catalog.ReadRemoteDeleteRejectionAsync(original.InstanceId, original.BatchId, original.Ordinal));
    }

    [TestMethod]
    [DataRow("destructive-policy")]
    [DataRow("unqualified-publication")]
    [DataRow("noncanonical-digest")]
    [DataRow("traversal")]
    public async Task PreservingDeleteCannotAdmitAnUnqualifiedOrDestructivePolicy(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await RejectionAdmissionAsync(catalog);
        var substitute = changed switch
        {
            "destructive-policy" => original with { Policy = original.Policy with { PreserveUnsynchronizedLocalContent = false } },
            "unqualified-publication" => original with { PackageVersion = "0.1.0-preview.10", PackageSource = "cfd98515dcc03ac5767a6820aed046a178410a53" },
            "noncanonical-digest" => original with { EntryFingerprint = new('a', 64) },
            _ => original with { RelativePath = "Docs/../other.txt" }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareRemoteDeleteRejectionAsync(substitute));
        Assert.IsEmpty(await catalog.ReadPendingRemoteDeleteRejectionsAsync(original.RootId));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("member")]
    [DataRow("mapping")]
    public async Task PreservingDeleteCannotBorrowAnotherRootOrMissingMembership(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await RejectionAdmissionAsync(catalog);
        var substitute = changed switch
        {
            "root" => original with { RootId = RootId.New() },
            "member" => original with { RootMemberId = Guid.NewGuid() },
            _ => original with { RootDirectoryName = "Peer", RelativePath = "Peer/missing.txt" }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRemoteDeleteRejectionAsync(substitute));
        Assert.IsEmpty(await catalog.ReadPendingRemoteDeleteRejectionsAsync(original.RootId));
    }

    [TestMethod]
    public async Task PreservingDeleteCannotReuseAnotherOriginalConflictOrChangeItsObservedOrder()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await RejectionAdmissionAsync(catalog); await catalog.PrepareRemoteDeleteRejectionAsync(original);
        var outcome = new MirrorPulseRemoteDeleteRejectionOutcome(original.OperationId, Guid.NewGuid(), original.StartedAt.AddSeconds(1), original.StartedAt.AddSeconds(2));
        foreach (var invalid in new[] { outcome with { OperationId = Guid.NewGuid() }, outcome with { ConflictId = Guid.Empty },
            outcome with { ConflictDetectedAt = original.StartedAt.AddTicks(-1) }, outcome with { ObservedAt = original.StartedAt } })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.CompleteRemoteDeleteRejectionAsync(original, invalid));
        await catalog.CompleteRemoteDeleteRejectionAsync(original, outcome);
        var next = original with { OperationId = Guid.NewGuid(), BatchId = original.InstanceId + "/next-original", ChangeId = "next-change" };
        await catalog.PrepareRemoteDeleteRejectionAsync(next);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteRemoteDeleteRejectionAsync(next,
            outcome with { OperationId = next.OperationId }));
        Assert.IsNull((await catalog.ReadRemoteDeleteRejectionAsync(next.InstanceId, next.BatchId, next.Ordinal))!.Outcome);
    }

    [TestMethod]
    [DataRow("admission")]
    [DataRow("index")]
    [DataRow("outcome")]
    public async Task PreservingDeleteReadsRefuseCorruptOriginalEvidence(string changed)
    {
        using var fixture = new Fixture(); MirrorPulseRemoteDeleteRejectionAdmission original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            original = await RejectionAdmissionAsync(catalog); await catalog.PrepareRemoteDeleteRejectionAsync(original);
            if (changed == "outcome") await catalog.CompleteRemoteDeleteRejectionAsync(original,
                new(original.OperationId, Guid.NewGuid(), original.StartedAt.AddSeconds(1), original.StartedAt.AddSeconds(2)));
        }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "admission" => "UPDATE remote_delete_rejections SET admission_payload=x'01';",
            "index" => "UPDATE remote_delete_rejections SET ordinal=1;",
            _ => "UPDATE remote_delete_rejections SET outcome_fingerprint=zeroblob(32);"
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadRemoteDeleteRejectionAsync(original.InstanceId,
            original.BatchId, changed == "index" ? 1 : original.Ordinal));
    }

    private static async Task<MirrorPulseRemoteDeleteRejectionAdmission> RejectionAdmissionAsync(MirrorPulseProductCatalog catalog)
    {
        var baseline = Baseline(); var protection = Protection(baseline);
        await ProtectAsync(catalog, baseline, protection);
        var member = await RegisterOriginalParentMembersAsync(catalog, baseline, protection);
        var instance = InstanceId.New();
        return new(1, Guid.NewGuid(), instance, member.RootId!.Value, "docs", "Docs", member.MemberId,
            instance + "/original-preserving-delete", new('A', 64), 2, 0, new('B', 64), "original-change", Guid.NewGuid(),
            "Docs/missing.txt", false, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion,
            MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit, new(true, true, TimeSpan.TicksPerMinute, 2), member.RegisteredAt.AddSeconds(1));
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("index")]
    public async Task PreservingDeleteSchema103MigratesEmpty102AndRefusesMissingCurrentOwnership(string missing)
    {
        using var fixture = new Fixture();
        await using (var fresh = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        string drop = missing == "table" ? "DROP TABLE remote_delete_rejections;" : "DROP INDEX ix_remote_delete_rejections_pending_root;";
        await ExecuteSqlAsync(fixture.Paths, drop + "PRAGMA user_version=102;");
        await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            Assert.IsEmpty(await migrated.ReadPendingRemoteDeleteRejectionsAsync(RootId.New()));
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(106L, await version.ExecuteScalarAsync());
        }
        await ExecuteSqlAsync(fixture.Paths, drop);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    [TestMethod]
    public async Task PreservingDeleteOlderSchemaCannotInventAnOriginalPendingAttempt()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareRemoteDeleteRejectionAsync(await RejectionAdmissionAsync(catalog));
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=102;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    [TestMethod]
    public async Task PreservingDeleteCannotLoseItsSdkConflictToADecisionBeforeOriginalSettlement()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await RejectionAdmissionAsync(catalog); await catalog.PrepareRemoteDeleteRejectionAsync(original);
        var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), original.InstanceId, original.ChangeId, original.RelativePath,
            MirrorPulseConflictReason.MissingItem, MirrorPulseVersionComparison.Unknown, null, "v2", original.StartedAt.AddSeconds(1),
            source: MirrorPulseConflictSource.CfSharpRemote);
        await catalog.SaveRemoteConflictProjectionAsync(conflict);
        var decision = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepLocal, null,
            original.PackageVersion, original.PackageSource);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareRemoteConflictActionAsync(decision));
        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(decision.CommandId));
        await catalog.CompleteRemoteDeleteRejectionAsync(original,
            new(original.OperationId, conflict.ConflictId, conflict.DetectedAt, conflict.DetectedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.PrepareRemoteConflictActionAsync(decision));
    }
}
