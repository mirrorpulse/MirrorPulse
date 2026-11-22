using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OriginalRemoteTreeSettlementReopensBetweenBoundedPagesAndRetainsDistinctRetiredHistory()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        MirrorPulseNamespaceRemoteRetirementTreeEntry[] entries; MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome;
        MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement[] firstPage;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (batch, entries, outcome) = await PrepareTreeSettlementAsync(first, 65);
            Assert.AreEqual(outcome, await first.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome));
            firstPage = entries.Reverse().Take(64).Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray();
            var partial = await first.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, firstPage);
            Assert.AreEqual(1, partial.NextOrdinal); Assert.IsNull(partial.CompletedAt);
            var inventory = await first.ReadNamespaceCurrentTreeSnapshotAsync(batch.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value);
            Assert.HasCount(64, inventory.RetiredTreeMembers!); Assert.HasCount(4, inventory.Members);
            Assert.IsEmpty(inventory.RetiredMembers); CollectionAssert.Contains(inventory.FencedRoots.ToArray(), batch.RootId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId,
                TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5)));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var reopened = (await second.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!;
        Assert.AreEqual(1, reopened.NextOrdinal); Assert.AreEqual(outcome, await second.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        Assert.AreEqual(reopened, await second.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, firstPage));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            [firstPage[0] with { VerifiedAt = firstPage[0].VerifiedAt.AddSeconds(1) }]));
        var lastPage = entries.Take(2).Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray();
        Assert.AreEqual(-1, (await second.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, lastPage)).NextOrdinal);
        CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), batch.RootId);
        var complete = await second.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5));
        Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, complete.Phase);
        var final = await second.ReadNamespaceCurrentTreeSnapshotAsync(batch.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value);
        Assert.HasCount(66, final.RetiredTreeMembers!); Assert.HasCount(2, final.Members); Assert.IsEmpty(final.FencedRoots);
        Assert.IsEmpty(final.RetiredMembers);
        foreach (var entry in entries)
        {
            Assert.AreEqual(entry.Original.Context.Member, await second.ReadNamespaceMemberAsync(entry.Original.Context.Member.MemberId));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => second.ReadNamespaceMemberLocationAsync(entry.Original.Context.Member.MemberId));
            Assert.IsNull(await second.ReadNamespaceRetirementAsync(batch.MemberOperationId(entry.Original.Context.Member.MemberId)));
        }
        var completeReplay = await second.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(6));
        Assert.AreEqual(JsonSerializer.Serialize(complete, TreeStartFixtureOptions), JsonSerializer.Serialize(completeReplay, TreeStartFixtureOptions));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task OriginalRemoteTreeConflictRetainsLiveMembersAndRecordsActuallyRemovedDescendants()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, applied) = await PrepareTreeSettlementAsync(catalog, 3, recordOutcome: false);
        var conflict = TreeConflictOutcome(batch, applied);
        await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(conflict);
        var members = entries.Reverse().Select(entry => TreeMemberSettlement(batch, conflict, entry, missing: entry.Ordinal is 1 or 3)).ToArray();
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, members);
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, conflict), conflict.VerifiedAt.AddSeconds(5));
        var current = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(batch.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value);
        Assert.HasCount(2, current.RetiredTreeMembers!); Assert.HasCount(4, current.Members); Assert.IsEmpty(current.FencedRoots);
        Assert.AreEqual(conflict, await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entries[2].Original.Context.Member.MemberId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(batch.MemberId));
        Assert.IsFalse(current.RetiredTreeMembers!.Any(member => member.Ordinal == 0));
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("batch")]
    [DataRow("tree")]
    [DataRow("start")]
    [DataRow("prefix")]
    [DataRow("change")]
    [DataRow("cursor")]
    [DataRow("batch-status")]
    [DataRow("entry-status")]
    [DataRow("native")]
    [DataRow("projection")]
    [DataRow("revision")]
    [DataRow("parent")]
    [DataRow("false-conflict")]
    public async Task OriginalRemoteTreeOutcomeRejectsUnboundOrContradictorySdkResults(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, _, outcome) = await PrepareTreeSettlementAsync(catalog, 1, recordOutcome: false);
        var invalid = changed switch
        {
            "operation" => outcome with { OperationId = Guid.Empty },
            "batch" => outcome with { BatchFingerprint = new string('B', 64) },
            "tree" => outcome with { TreeFingerprint = new string('B', 64) },
            "start" => outcome with { StartedAt = outcome.StartedAt.AddSeconds(-1) },
            "prefix" => outcome with { AppliedEntryCount = outcome.AppliedEntryCount + 1 },
            "change" => outcome with { LastAppliedChangeId = "different" },
            "cursor" => outcome with { SafeCursorFingerprint = "not-a-fingerprint" },
            "batch-status" => outcome with { SdkBatchCompleted = true },
            "entry-status" => outcome with { EntryStatus = "Failed" },
            "native" => outcome with { NativeMissing = false },
            "projection" => outcome with { OfficialTombstone = false },
            "revision" => outcome with { OfficialRemoteRevision = "v1" },
            "parent" => outcome with { Parent = outcome.Parent with { LocalObject = outcome.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            _ => outcome with
            {
                EntryStatus = "Conflict",
                ConflictId = Guid.NewGuid(),
                ConflictDetectedAt = outcome.VerifiedAt,
                ConflictReason = "StaleRemoteRevision"
            }
        };
        if (changed == "operation") await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(invalid));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        Assert.AreEqual(1, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.NextOrdinal);
    }

    [TestMethod]
    [DataRow("too-many")]
    [DataRow("skip")]
    [DataRow("ascending")]
    [DataRow("foreign-member")]
    [DataRow("retain-after-success")]
    [DataRow("child-revision")]
    [DataRow("parent")]
    [DataRow("time")]
    public async Task OriginalRemoteTreePageRejectsUnsafeDispositionWithoutReleasingAnyName(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, outcome) = await PrepareTreeSettlementAsync(catalog, 2);
        var member = TreeMemberSettlement(batch, outcome, entries[2]);
        MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement[] page = changed switch
        {
            "too-many" => Enumerable.Repeat(member, 65).ToArray(),
            "skip" => [TreeMemberSettlement(batch, outcome, entries[1])],
            "ascending" => [TreeMemberSettlement(batch, outcome, entries[1]), member],
            "foreign-member" => [member with { MemberId = Guid.NewGuid() }],
            "retain-after-success" => [TreeMemberSettlement(batch, outcome, entries[2], missing: false)],
            "child-revision" => [member with { OfficialRemoteRevision = batch.RemoteEntry!.RemoteRevision }],
            "parent" => [member with { Parent = member.Parent with { LocalObject = member.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } }],
            _ => [member with { VerifiedAt = outcome.VerifiedAt.AddSeconds(-1) }]
        };
        if (changed is "too-many" or "ascending") await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, page));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, page));
        Assert.AreEqual(2, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.NextOrdinal);
        foreach (var entry in entries) Assert.IsNotNull(await catalog.ReadNamespaceMemberLocationAsync(entry.Original.Context.Member.MemberId));
    }

    [TestMethod]
    [DataRow("outcome")]
    [DataRow("member")]
    [DataRow("name")]
    [DataRow("progress")]
    public async Task OriginalRemoteTreeSettlementRollsBackItsWholePersistenceBoundary(string failed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, outcome) = await PrepareTreeSettlementAsync(catalog, 2, recordOutcome: false);
        string action = failed switch
        {
            "outcome" => "BEFORE UPDATE ON namespace_remote_retirement_tree_settlements",
            "member" => "BEFORE INSERT ON namespace_remote_retirement_tree_member_settlements WHEN NEW.ordinal=1",
            "name" => "BEFORE DELETE ON namespace_member_names WHEN OLD.member_id='" + entries[1].Original.Context.Member.MemberId.ToString("D") + "'",
            _ => "BEFORE UPDATE ON namespace_remote_retirement_tree_settlements WHEN NEW.next_ordinal=0"
        };
        if (failed != "outcome") await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fixture_tree_settlement " + action + " BEGIN SELECT RAISE(ABORT,'synthetic tree settlement boundary'); END;");
        if (failed == "outcome")
        {
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome));
            Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        }
        else await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            entries.Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray()));
        Assert.AreEqual(2, (await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.NextOrdinal);
        foreach (var entry in entries) Assert.IsNotNull(await catalog.ReadNamespaceMemberLocationAsync(entry.Original.Context.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fixture_tree_settlement;");
        await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome);
        Assert.AreEqual(-1, (await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            entries.Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray())).NextOrdinal);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("outcome")]
    [DataRow("member")]
    [DataRow("fingerprint")]
    [DataRow("restored-name")]
    public async Task OriginalRemoteTreeSettlementCannotLoseHistoryOrRestoreRemovedNamesAcrossReopen(string lost)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            MirrorPulseNamespaceRemoteRetirementTreeEntry[] entries; MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome;
            (batch, entries, outcome) = await PrepareTreeSettlementAsync(first, 1);
            await first.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, [TreeMemberSettlement(batch, outcome, entries[1])]);
        }
        string sql = lost switch
        {
            "owner" => "DELETE FROM namespace_remote_retirement_tree_settlements;",
            "outcome" => "DELETE FROM namespace_remote_retirement_tree_outcomes;",
            "member" => "DELETE FROM namespace_remote_retirement_tree_member_settlements;",
            "fingerprint" => "UPDATE namespace_remote_retirement_tree_member_settlements SET fingerprint=zeroblob(32);",
            _ => "INSERT INTO namespace_member_names(member_id,parent_member_id,child_name_key) VALUES('" + batch.Entries[1].MemberId.ToString("D") +
                "','" + batch.MemberId.ToString("D") + "','RESTORED');"
        };
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + sql);
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRetirementBatchAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceCurrentTreeSnapshotAsync(batch.RemoteEntry!.Parent.Context.Location.ParentMemberId!.Value));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Schema72UpgradeCreatesOnlyTheMissingSettlementOwnerAndNeverInventsAnSdkResult(bool started)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        MirrorPulseNamespaceRemoteRetirementTreeStart? originalStart;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteTreeAsync(first, 1);
            batch = await first.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
            originalStart = started ? await first.RecordNamespaceRemoteRetirementTreeStartAsync(batch.OperationId, batch.PreparedAt) : null;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_retirement_tree_member_settlements; DROP TABLE namespace_remote_retirement_tree_outcomes; DROP TABLE namespace_remote_retirement_tree_settlements; PRAGMA user_version=72;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(originalStart, await second.ReadNamespaceRemoteRetirementTreeStartAsync(batch.OperationId));
        Assert.IsNull(await second.ReadNamespaceRemoteRetirementTreeOutcomeAsync(batch.OperationId));
        var progress = await second.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId);
        if (started) { Assert.IsNotNull(progress); Assert.AreEqual(1, progress.NextOrdinal); Assert.IsNull(progress.OutcomeFingerprint); }
        else Assert.IsNull(progress);
        CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), batch.RootId);
    }

    [TestMethod]
    public async Task OriginalRemoteTreeCompletionCannotReleaseItsFenceBeforeTheFinalAtomicCommit()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, outcome) = await PrepareTreeSettlementAsync(catalog, 1);
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, entries.Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray());
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fixture_tree_completion BEFORE UPDATE ON namespace_remote_retirement_tree_settlements WHEN NEW.next_ordinal=-1 BEGIN SELECT RAISE(ABORT,'synthetic completion boundary'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId,
            TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5)));
        Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Prepared, (await catalog.ReadNamespaceRetirementBatchAsync(batch.OperationId))!.Phase);
        Assert.IsNull((await catalog.ReadNamespaceRemoteRetirementTreeSettlementAsync(batch.OperationId))!.CompletedAt);
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), batch.RootId);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fixture_tree_completion;");
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    private static async Task<(MirrorPulseNamespaceRetirementBatch Batch, MirrorPulseNamespaceRemoteRetirementTreeEntry[] Entries,
        MirrorPulseNamespaceRemoteRetirementTreeOutcome Outcome)> PrepareTreeSettlementAsync(MirrorPulseProductCatalog catalog,
        int children, bool recordOutcome = true)
    {
        var setup = await PrepareRemoteTreeAsync(catalog, children);
        var batch = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        var start = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(batch.OperationId, batch.PreparedAt.AddSeconds(1));
        var outcome = TreeAppliedOutcome(batch, start);
        if (recordOutcome) await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome);
        return (batch, setup.Entries, outcome);
    }

    private static MirrorPulseNamespaceRemoteRetirementTreeOutcome TreeAppliedOutcome(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeStart start)
    {
        var remote = batch.RemoteEntry!; DateTimeOffset observed = start.StartedAt.AddSeconds(2);
        return new(1, batch.OperationId, remote.BatchFingerprint, batch.RemoteTreeFingerprint!, start.StartedAt,
            remote.Ordinal + 1, remote.ChangeId, Convert.ToHexString(SHA256.HashData(new byte[] { 2 })),
            remote.Ordinal + 1 == remote.BatchEntryCount, "Applied", null, null, null, observed, true, true, remote.RemoteRevision,
            remote.Parent.Context.Verification with { ObservedAt = observed }, null);
    }

    private static MirrorPulseNamespaceRemoteRetirementTreeOutcome TreeConflictOutcome(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeOutcome applied) => applied with
        {
            EntryStatus = "Conflict",
            ConflictId = Guid.NewGuid(),
            ConflictDetectedAt = applied.VerifiedAt,
            ConflictReason = "PendingLocalOperation",
            NativeMissing = false,
            OfficialTombstone = false,
            OfficialRemoteRevision = batch.RemoteEntry!.OfficialRemoteRevision,
            Child = batch.RemoteEntry.Child.Context.Verification with { ObservedAt = applied.VerifiedAt }
        };

    private static MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement TreeMemberSettlement(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome, MirrorPulseNamespaceRemoteRetirementTreeEntry entry, bool missing = true)
    {
        DateTimeOffset observed = outcome.VerifiedAt.AddSeconds(1);
        return new(1, batch.OperationId, entry.Ordinal, entry.Original.Context.Member.MemberId, missing, missing,
            missing && entry.Ordinal == 0 ? batch.RemoteEntry!.RemoteRevision : entry.OfficialRemoteRevision, observed,
            batch.RemoteEntry!.Parent.Context.Verification with { ObservedAt = observed },
            missing ? null : entry.Original.Context.Verification with { ObservedAt = observed });
    }

    private static MirrorPulseNamespacePermissionVerification TreeCompletionParent(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome) => batch.RemoteEntry!.Parent.Context.Verification with { ObservedAt = outcome.VerifiedAt.AddSeconds(4) };
}
