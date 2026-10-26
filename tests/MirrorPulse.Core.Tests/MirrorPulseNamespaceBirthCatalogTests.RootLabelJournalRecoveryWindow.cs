using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RootLabelRecoveryWindowRetriesTheSameFailedOriginalAcrossCursorWrapWithoutAdmittingRootWork(bool enabled)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelReplayStartAsync(catalog, enabled);
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        var failure = new IOException("Simulated lost public ACK return.");
        bool accept = false;
        var calls = new List<Guid>();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
        {
            actor.AssertAccess(); calls.Add(id);
            return accept ? Task.CompletedTask : Task.FromException(failure);
        }, clock);
        for (int turn = 0; turn < 2; turn++)
        {
            var page = await recovery.RecoverPendingAsync(limit: 1);
            Assert.HasCount(1, page); Assert.AreSame(failure, page[0].Failure);
            Assert.AreEqual(original.Observation.OperationId, page[0].OperationId);
            Assert.IsTrue(recovery.RecoveryScanInProgress);
            Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
            CollectionAssert.AreEqual(new[] { original.Witness.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        accept = true;
        var success = await recovery.RecoverPendingAsync(limit: 1);
        Assert.HasCount(1, success); Assert.IsNull(success[0].Failure);
        Assert.AreEqual(original with { AcknowledgedAt = clock.Now }, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        Assert.IsEmpty(await recovery.RecoverPendingAsync(limit: 1));
        Assert.IsFalse(recovery.RecoveryScanInProgress);
        CollectionAssert.AreEqual(Enumerable.Repeat(original.Observation.OperationId, 3).ToArray(), calls);
        Assert.AreEqual(enabled, (await catalog.ReadAdapterTopologyAsync()).Instances.Single().Enabled);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RootLabelRecoveryWindowRetainsEarlierFailuresAfterASuccessfulPartialLastPage()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
        var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
        var auditedAt = receipt.Root.VerifiedAt.AddSeconds(3);
        var original = await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
            auditedAt, RootLabelNativeIdentity(setup, auditedAt), RootLabelObservation(setup, auditedAt) with
            { OperationId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") }, auditedAt.AddSeconds(1));
        await using var writer = Connection(fixture.Paths); await writer.OpenAsync();
        await AddForeignRootLabelRowsAsync(writer, original.Observation.OperationId, 1, 4);
        string ForeignBytes(string all) => string.Join('\n', all.Split('\n').Where(line => !line.StartsWith(original.Observation.OperationId.ToString("D"), StringComparison.Ordinal)));
        string before = ForeignBytes(await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths));
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        var calls = new List<Guid>();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
        { actor.AssertAccess(); calls.Add(id); return Task.CompletedTask; }, clock);
        Assert.HasCount(2, await catalog.ReadPendingRootLabelJournalOperationIdsAsync(limit: 2));
        for (int turn = 0; turn < 2; turn++)
        {
            var failed = await recovery.RecoverPendingAsync(limit: 2);
            Assert.HasCount(2, failed); Assert.IsTrue(failed.All(attempt => attempt.Failure is InvalidDataException));
            Assert.IsEmpty(calls); Assert.IsTrue(recovery.RecoveryScanInProgress);
        }
        var last = await recovery.RecoverPendingAsync(limit: 2);
        Assert.HasCount(1, last); Assert.IsNull(last[0].Failure);
        Assert.AreEqual(original.Observation.OperationId, last[0].OperationId);
        Assert.IsTrue(recovery.RecoveryScanInProgress);
        CollectionAssert.AreEqual(new[] { original.Observation.OperationId }, calls);
        var revisited = await recovery.RecoverPendingAsync(limit: 2);
        Assert.HasCount(2, revisited); Assert.IsTrue(revisited.All(attempt => attempt.Failure is InvalidDataException));
        Assert.AreEqual(before, ForeignBytes(await ReadRootLabelJournalOriginalBytesAsync(fixture.Paths)));
        Assert.AreEqual(original with { AcknowledgedAt = clock.Now }, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        // Progress in metadata settlement cannot turn globally damaged ownership into native admission.
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RootLabelRecoveryWindowRetainsFailedSqliteReceiptAndRequiresAnotherActualCallbackReturn()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        int calls = 0;
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, async (id, token) =>
        {
            actor.AssertAccess(); Assert.AreEqual(original.Observation.OperationId, id);
            if (++calls == 1)
                await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER reject_window_receipt BEFORE UPDATE ON root_label_journal_acknowledgements BEGIN SELECT RAISE(ABORT,'simulated lost receipt'); END;");
        }, clock);
        var failed = await recovery.RecoverPendingAsync();
        Assert.HasCount(1, failed); Assert.IsInstanceOfType<SqliteException>(failed[0].Failure);
        Assert.IsTrue(recovery.RecoveryScanInProgress);
        Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_window_receipt;");
        clock.Now = clock.Now.AddSeconds(1);
        var retried = await recovery.RecoverPendingAsync();
        Assert.HasCount(1, retried); Assert.IsNull(retried[0].Failure); Assert.AreEqual(2, calls);
        Assert.IsFalse(recovery.RecoveryScanInProgress);
        Assert.AreEqual(original with { AcknowledgedAt = clock.Now }, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
    }

    [TestMethod]
    public async Task RootLabelRecoveryWindowPropagatesCallerCancellationWithoutInventingAReceipt()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
        {
            actor.AssertAccess(); Assert.AreEqual(original.Observation.OperationId, id); calls++;
            cancellation.Cancel(); return Task.FromException(new OperationCanceledException(cancellation.Token));
        });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await recovery.RecoverPendingAsync(token: cancellation.Token));
        Assert.AreEqual(1, calls); Assert.IsTrue(recovery.RecoveryScanInProgress);
        Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        CollectionAssert.AreEqual(new[] { original.Witness.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task RootLabelRecoveryWindowWithNoOriginalNeverCallsThePublicReturnOrKeepsTheFeedAwake()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor,
            (id, token) => throw new AssertFailedException("An empty metadata roster cannot invent a public ACK."));
        Assert.IsEmpty(await catalog.ReadPendingRootLabelJournalOperationIdsAsync());
        Assert.IsEmpty(await recovery.RecoverPendingAsync()); Assert.IsFalse(recovery.RecoveryScanInProgress);
        Assert.IsFalse(await recovery.TryReplayOwnedAsync(Guid.NewGuid()));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
