using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OriginalRemoteRetirementSettlesOnlyItsOwnActualOutcomeAcrossOwners(bool directory, bool conflict)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRetirementIntent intent;
        MirrorPulseNamespaceRemoteRetirementOutcome outcome;
        MirrorPulseNamespaceRetirement terminal;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first, directory);
            intent = await RemoteRetirementAsync(first, setup.Member);
            var prepared = await first.PrepareNamespaceRetirementAsync(intent);
            Assert.AreEqual(prepared, await first.PrepareNamespaceRetirementAsync(intent));
            Assert.IsTrue(await first.CanResumeNamespaceRemoteRetirementAsync(intent));
            Assert.HasCount(1, await first.ReadPendingNamespaceRemoteRetirementsAsync(intent.RemoteEntry!.InstanceId, intent.RemoteEntry.BatchId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt));
            await first.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry.ItemId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteNamespaceRetirementAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1)));
            outcome = RemoteRetirementOutcome(intent, conflict);
            terminal = await first.SettleNamespaceRemoteRetirementAsync(outcome);
            Assert.AreEqual(conflict ? MirrorPulseNamespaceRetirementPhase.RemoteRejected : MirrorPulseNamespaceRetirementPhase.Completed, terminal.Phase);
            Assert.AreEqual(terminal, await first.SettleNamespaceRemoteRetirementAsync(outcome));
            Assert.IsFalse(await first.CanResumeNamespaceRemoteRetirementAsync(intent));
            Assert.HasCount(0, await first.ReadPendingNamespaceRemoteRetirementsAsync(intent.RemoteEntry.InstanceId, intent.RemoteEntry.BatchId));
            CollectionAssert.DoesNotContain((await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), intent.RootId);
            Assert.AreEqual(conflict ? setup.Member : null, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(terminal, await second.ReadNamespaceRemoteRetirementAsync(intent.RemoteEntry!.InstanceId, intent.RemoteEntry.BatchId, intent.RemoteEntry.Ordinal));
        Assert.AreEqual(outcome, await second.ReadNamespaceRemoteRetirementOutcomeAsync(intent.OperationId));
        Assert.AreEqual(terminal, await second.SettleNamespaceRemoteRetirementAsync(outcome));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.SettleNamespaceRemoteRetirementAsync(outcome with { VerifiedAt = outcome.VerifiedAt.AddSeconds(1) }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("tombstone")]
    [DataRow("revision")]
    [DataRow("child")]
    [DataRow("parent-binding")]
    [DataRow("parent-dacl")]
    [DataRow("parent-time")]
    [DataRow("before-start")]
    public async Task AppliedRemoteRetirementRejectsIncompleteOrForeignFacts(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        var good = RemoteRetirementOutcome(intent, false);
        var bad = invalid switch
        {
            "missing" => good with { NativeMissing = false },
            "tombstone" => good with { OfficialTombstone = false },
            "revision" => good with { OfficialRemoteRevision = "foreign" },
            "child" => good with { Child = intent.RemoteEntry.Child.Context.Verification },
            "parent-binding" => good with { Parent = good.Parent with { LocalObject = good.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent-dacl" => good with { Parent = good.Parent with { Dacl = "D:P" } },
            "parent-time" => good with { Parent = good.Parent with { ObservedAt = intent.PreparedAt.AddSeconds(-1) } },
            _ => good with { VerifiedAt = intent.PreparedAt.AddSeconds(-1) }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementAsync(bad));
        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Prepared, (await catalog.ReadNamespaceRetirementAsync(intent.OperationId))!.Phase);
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(intent.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), intent.RootId);
    }

    [TestMethod]
    [DataRow("missing-child")]
    [DataRow("changed-child")]
    [DataRow("missing-native")]
    [DataRow("tombstone")]
    [DataRow("conflict-time")]
    [DataRow("revision")]
    public async Task RejectedRemoteRetirementPreservesTheActualOriginalChildAndConflict(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        var good = RemoteRetirementOutcome(intent, true);
        var bad = invalid switch
        {
            "missing-child" => good with { Child = null },
            "changed-child" => good with { Child = good.Child! with { LocalObject = good.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "missing-native" => good with { NativeMissing = true },
            "tombstone" => good with { OfficialTombstone = true },
            "conflict-time" => good with { ConflictDetectedAt = intent.PreparedAt.AddSeconds(-1) },
            _ => good with { OfficialRemoteRevision = "foreign" }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementAsync(bad));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(intent.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    public async Task OriginalRemoteRetirementAndEntryIndexRollbackTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER abort_remote_retirement BEFORE INSERT ON namespace_remote_retirement_entries BEGIN SELECT RAISE(ABORT,'synthetic boundary failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementAsync(intent.RemoteEntry!.InstanceId, intent.RemoteEntry.BatchId, intent.RemoteEntry.Ordinal));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task RemoteRetirementOutcomeAndNameReleaseRollbackTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER abort_remote_retirement_settlement BEFORE UPDATE ON namespace_member_retirements BEGIN SELECT RAISE(ABORT,'synthetic settlement failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(intent, false)));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementOutcomeAsync(intent.OperationId));
        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Prepared, (await catalog.ReadNamespaceRetirementAsync(intent.OperationId))!.Phase);
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    [DataRow("index")]
    [DataRow("outcome")]
    public async Task OriginalRemoteRetirementRejectsCorruptHistoricalEvidence(string corrupt)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        await catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(intent, false));
        await ExecuteSqlAsync(fixture.Paths, corrupt == "index"
            ? "UPDATE namespace_remote_retirement_entries SET batch_fingerprint='" + new string('B', 64) + "';"
            : "DELETE FROM namespace_remote_retirement_outcomes;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementAsync(intent.RemoteEntry.InstanceId, intent.RemoteEntry.BatchId, intent.RemoteEntry.Ordinal));
    }

    [TestMethod]
    public async Task CompletedRemoteBirthEntryCannotBeReassignedToRetirement()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog);
        var born = await RegisterRemoteBornAsync(catalog, birth);
        var intent = await RemoteRetirementAsync(catalog, born.Member);
        intent = intent with
        {
            RemoteEntry = intent.RemoteEntry! with
            {
                InstanceId = birth.RemoteEntry!.InstanceId,
                BatchId = birth.RemoteEntry.BatchId,
                BatchFingerprint = birth.RemoteEntry.BatchFingerprint,
                BatchEntryCount = birth.RemoteEntry.BatchEntryCount,
                Ordinal = birth.RemoteEntry.Ordinal,
                ChangeId = birth.RemoteEntry.ChangeId
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        Assert.AreEqual(birth, await catalog.ReadNamespaceRemoteBirthAsync(birth.RemoteEntry.InstanceId, birth.RemoteEntry.BatchId, birth.RemoteEntry.Ordinal));
    }

    [TestMethod]
    public async Task RejectedRemoteRetirementHistoryCannotBeReassignedToMoveOrBirth()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        await catalog.PrepareNamespaceRetirementAsync(intent);
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, intent.RemoteEntry!.ItemId);
        await catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(intent, true));
        var remote = intent.RemoteEntry;
        var move = setup.Move with
        {
            Version = 2,
            RemoteEntry = new(1, remote.InstanceId, remote.BatchId, remote.BatchFingerprint,
            remote.BatchEntryCount, remote.Ordinal, remote.ChangeId, remote.PreviousRemoteRevision, remote.RemoteRevision, remote.NativeRemoteRevision)
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
        var birth = Birth(setup.Parent, setup.ParentProof) with
        {
            Version = 3,
            Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation,
            RelativePath = "Docs/another.txt",
            RemoteEntry = RemoteBirthReference(setup.ParentMember.MemberId) with
            {
                InstanceId = remote.InstanceId,
                BatchId = remote.BatchId,
                BatchFingerprint = remote.BatchFingerprint,
                BatchEntryCount = remote.BatchEntryCount,
                Ordinal = remote.Ordinal,
                ChangeId = remote.ChangeId
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task Schema69OrdinaryRetirementGainsNoInventedRemoteOwner()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirement original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first);
            original = await first.PrepareNamespaceRetirementAsync(Retire(setup, Guid.NewGuid()));
        }
        await ExecuteSqlAsync(fixture.Paths, """
            DROP TABLE namespace_remote_retirement_outcomes;
            DROP TABLE namespace_remote_retirement_entries;
            ALTER TABLE namespace_member_retirements RENAME TO retirement_fixture_current;
            CREATE TABLE namespace_member_retirements(
                operation_id TEXT NOT NULL PRIMARY KEY,member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3)));
            INSERT INTO namespace_member_retirements(rowid,operation_id,member_id,payload,fingerprint,phase)
                SELECT rowid,operation_id,member_id,payload,fingerprint,phase FROM retirement_fixture_current;
            DROP TABLE retirement_fixture_current;
            PRAGMA user_version=69;
            """);
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await second.ReadNamespaceRetirementAsync(original.OperationId));
        Assert.IsNull((await second.ReadNamespaceRetirementAsync(original.OperationId))!.Intent.RemoteEntry);
        var instance = InstanceId.New();
        Assert.HasCount(0, await second.ReadPendingNamespaceRemoteRetirementsAsync(instance, instance + "/delete"));
        CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), original.Intent.RootId);
    }

    private static async Task<MirrorPulseNamespaceRetirementIntent> RemoteRetirementAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member)
    {
        var child = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!;
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.Context.Location.ParentMemberId!.Value))!;
        var instance = InstanceId.New();
        DateTimeOffset prepared = new[] { DateTimeOffset.UtcNow, child.Context.Verification.ObservedAt, parent.Context.Verification.ObservedAt }.Max().AddSeconds(1);
        return new(2, Guid.NewGuid(), member.MemberId, member.RootId!.Value, member.LocalObject,
            child.Context.Location.RelativePath, member.IsDirectory, member.OwnerSid, child.Context.Protection.RoleSid, prepared)
        {
            RemoteEntry = new(1, instance, instance + "/delete", new string('A', 64), 4, 1, "original-delete",
            child.Identity.ItemId!.Value, child.Identity.RemoteId!, "v1", "deleted-v2", "v1", "v1", child, parent,
            "0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16", new(true, true, TimeSpan.FromMinutes(5).Ticks, 2))
        };
    }

    private static MirrorPulseNamespaceRemoteRetirementOutcome RemoteRetirementOutcome(MirrorPulseNamespaceRetirementIntent intent, bool conflict)
    {
        var remote = intent.RemoteEntry!; DateTimeOffset observed = intent.PreparedAt.AddSeconds(2);
        return new(1, intent.OperationId, conflict ? Guid.NewGuid() : null, conflict ? intent.PreparedAt.AddSeconds(1) : null,
            observed.AddSeconds(1), !conflict, !conflict, conflict ? remote.OfficialRemoteRevision : remote.RemoteRevision,
            remote.Parent.Context.Verification with { ObservedAt = observed },
            conflict ? remote.Child.Context.Verification with { ObservedAt = observed } : null);
    }
}
