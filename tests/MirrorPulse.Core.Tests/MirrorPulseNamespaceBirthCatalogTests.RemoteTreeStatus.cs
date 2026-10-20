using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OriginalDirectoryStatusSeparatesUnknownInvocationRootDeletionAndMemberCompletionAcrossReopen()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome; MirrorPulseNamespaceRemoteRetirementTreeEntry[] entries;
        MirrorPulseNamespaceRemoteRetirementTreeStatus partial;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.IsNull(await first.ReadNamespaceRemoteRetirementTreeStatusAsync(Guid.NewGuid()));
            var setup = await PrepareRemoteTreeAsync(first, 2); entries = setup.Entries;
            batch = await first.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, entries, setup.Intent.PreparedAt);
            var admitted = (await first.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
            Assert.AreEqual("Admitted", admitted.Stage); Assert.IsNull(admitted.StartedAt); Assert.IsNull(admitted.RootNativeDeleted);
            Assert.IsNull(admitted.RootOutcome); Assert.AreEqual(0, admitted.SettledMembers); Assert.IsNull(admitted.CompletedAt);
            var start = await first.RecordNamespaceRemoteRetirementTreeStartAsync(batch.OperationId, batch.PreparedAt);
            var unknown = (await first.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
            Assert.AreEqual("InvocationMayHaveBegun", unknown.Stage); Assert.AreEqual(start.StartedAt, unknown.StartedAt);
            Assert.IsNull(unknown.RootNativeDeleted); Assert.IsNull(unknown.RootOfficialTombstone);
            outcome = TreeAppliedOutcome(batch, start) with { EntryStatus = "CommittedPrefix" };
            await first.RecordNamespaceRemoteRetirementTreeOutcomeAsync(outcome);
            await first.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, [TreeMemberSettlement(batch, outcome, entries[2])]);
            partial = (await first.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
            Assert.AreEqual("SettlingMembers", partial.Stage); Assert.IsTrue(partial.RootNativeDeleted);
            Assert.AreEqual("CommittedPrefix", partial.RootOutcome); Assert.AreEqual(1, partial.SettledMembers);
            Assert.AreEqual(1, partial.RemovedMembers); Assert.AreEqual(0, partial.RetainedMembers); Assert.AreEqual(1, partial.NextOrdinal);
            Assert.AreEqual(3, partial.MemberCount); Assert.IsNull(partial.CompletedAt);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(partial, await second.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId));
        await second.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, entries.Take(2).Reverse().Select(entry => TreeMemberSettlement(batch, outcome, entry)).ToArray());
        var waiting = (await second.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
        Assert.AreEqual("AwaitingCompletion", waiting.Stage); Assert.IsNull(waiting.CompletedAt); Assert.AreEqual(3, waiting.SettledMembers);
        await second.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, outcome), outcome.VerifiedAt.AddSeconds(5));
        var completed = (await second.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
        Assert.AreEqual("Completed", completed.Stage); Assert.IsNotNull(completed.CompletedAt);
        Assert.IsTrue(completed.RootNativeDeleted); Assert.AreEqual(3, completed.RemovedMembers);
        Assert.AreEqual(batch.RemoteEntry!.PackageVersion, completed.PackageVersion); Assert.AreEqual(batch.RemoteEntry.PackageSource, completed.PackageSource);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task CompletedConflictingDirectoryStatusNeverReportsItsRetainedRootAsDeleted()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, applied) = await PrepareTreeSettlementAsync(catalog, 2, recordOutcome: false);
        var conflict = TreeConflictOutcome(batch, applied);
        await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(conflict);
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
            entries.Reverse().Select(entry => TreeMemberSettlement(batch, conflict, entry, missing: entry.Ordinal == 1)).ToArray());
        await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, conflict), conflict.VerifiedAt.AddSeconds(5));
        var status = (await catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
        Assert.AreEqual("Completed", status.Stage); Assert.IsFalse(status.RootNativeDeleted);
        Assert.IsFalse(status.RootOfficialTombstone); Assert.AreEqual("Conflict", status.RootOutcome);
        Assert.AreEqual(conflict.ConflictId, status.ConflictId); Assert.AreEqual(conflict.ConflictReason, status.ConflictReason);
        Assert.AreEqual(3, status.SettledMembers); Assert.AreEqual(1, status.RemovedMembers); Assert.AreEqual(2, status.RetainedMembers);
        Assert.IsNotNull(await catalog.ReadNamespaceMemberLocationAsync(batch.MemberId));
    }

    [TestMethod]
    public async Task DirectoryStatusPagesResumeWithinOneRootAndExcludeControlledRetirements()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 1); var expected = new HashSet<Guid>();
        for (int index = 0; index < 5; index++)
        {
            var remote = setup.Intent.RemoteEntry! with { BatchId = setup.Intent.RemoteEntry.InstanceId + "/status-" + index };
            var batch = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(Guid.NewGuid(), remote, setup.Entries, setup.Intent.PreparedAt.AddMinutes(index));
            expected.Add(batch.OperationId);
            var start = await catalog.RecordNamespaceRemoteRetirementTreeStartAsync(batch.OperationId, batch.PreparedAt);
            var conflict = TreeConflictOutcome(batch, TreeAppliedOutcome(batch, start));
            await catalog.RecordNamespaceRemoteRetirementTreeOutcomeAsync(conflict);
            await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId,
                setup.Entries.Reverse().Select(entry => TreeMemberSettlement(batch, conflict, entry, missing: false)).ToArray());
            await catalog.CompleteNamespaceRemoteRetirementTreeAsync(batch.OperationId, TreeCompletionParent(batch, conflict), conflict.VerifiedAt.AddSeconds(5));
        }
        var controlled = await catalog.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Intent.MemberId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(controlled.OperationId));
        var observed = new HashSet<Guid>(); Guid? after = null; int pages = 0;
        do
        {
            var page = await catalog.ReadNamespaceRemoteRetirementTreeStatusesAsync(setup.Intent.RootId, after, 2);
            Assert.IsTrue(page.Entries.Count is >= 1 and <= 2);
            foreach (var entry in page.Entries) { Assert.AreEqual(setup.Intent.RootId, entry.RootId); Assert.IsTrue(observed.Add(entry.OperationId)); }
            after = page.NextOperationId; pages++;
        } while (after is not null);
        Assert.AreEqual(3, pages); Assert.IsTrue(expected.SetEquals(observed));
        Assert.IsEmpty((await catalog.ReadNamespaceRemoteRetirementTreeStatusesAsync(RootId.New())).Entries);
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("root")]
    [DataRow("after")]
    [DataRow("zero")]
    [DataRow("large")]
    public async Task DirectoryStatusRejectsInvalidInputsWithoutInventingOperations(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (invalid == "operation") await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(Guid.Empty));
        else await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceRemoteRetirementTreeStatusesAsync(
            invalid == "root" ? new RootId(Guid.Empty) : RootId.New(), invalid == "after" ? Guid.Empty : null,
            invalid == "zero" ? 0 : invalid == "large" ? 65 : 64));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(Guid.NewGuid()));
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("member")]
    [DataRow("outcome")]
    public async Task DirectoryStatusAndListingFailClosedOnLostOwnedSettlementHistory(string missing)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (batch, entries, outcome) = await PrepareTreeSettlementAsync(catalog, 1);
        await catalog.SettleNamespaceRemoteRetirementTreePageAsync(batch.OperationId, [TreeMemberSettlement(batch, outcome, entries[1])]);
        string table = missing switch { "owner" => "namespace_remote_retirement_tree_settlements", "member" => "namespace_remote_retirement_tree_member_settlements", _ => "namespace_remote_retirement_tree_outcomes" };
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; DELETE FROM " + table + ";");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementTreeStatusesAsync(batch.RootId));
    }

    [TestMethod]
    public async Task Schema74UpgradeAddsIndexedStatusWithoutChangingOriginalRecoveryFacts()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRemoteRetirementTreeStatus original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (batch, _, _) = await PrepareTreeSettlementAsync(first, 1);
            original = (await first.ReadNamespaceRemoteRetirementTreeStatusAsync(batch.OperationId))!;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP INDEX namespace_retirement_batches_root_operation; PRAGMA user_version=74;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await second.ReadNamespaceRemoteRetirementTreeStatusAsync(original.OperationId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='namespace_retirement_batches_root_operation';";
        Assert.AreEqual(1L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(80L, await query.ExecuteScalarAsync());
    }
}
