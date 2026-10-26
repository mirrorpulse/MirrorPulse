using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RootLabelJournalReplayLostOrCanceledReturnRetainsTheFirstStartEvenIfTheSdkRowWasRemoved(bool cancel)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        using var cancellation = new CancellationTokenSource();
        bool sdkRowExists = true;
        var calls = new List<Guid>();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
        {
            actor.AssertAccess(); calls.Add(id); sdkRowExists = false;
            if (cancel) { cancellation.Cancel(); return Task.FromException(new OperationCanceledException(cancellation.Token)); }
            return Task.FromException(new IOException("Simulated lost public ACK return."));
        });
        if (cancel)
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await recovery.RecoverRootAsync(original.Witness.RootId, cancellation.Token));
        else
            await Assert.ThrowsExactlyAsync<IOException>(async () => await recovery.RecoverRootAsync(original.Witness.RootId));
        Assert.IsFalse(sdkRowExists); CollectionAssert.AreEqual(new[] { original.Observation.OperationId }, calls);
        Assert.AreEqual(original, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        CollectionAssert.AreEqual(new[] { original.Witness.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelJournalReplayActualCallbackReturnSettlesBeforeCallerCancellationWithoutGrantingAdmission(bool enabled)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement completed;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        await using (var actor = new MirrorPulseNamespaceActor())
        {
            var original = await PrepareRootLabelReplayStartAsync(catalog, enabled);
            using var cancellation = new CancellationTokenSource();
            var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
            int calls = 0;
            var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
            {
                actor.AssertAccess(); Assert.AreEqual(original.Observation.OperationId, id); calls++;
                cancellation.Cancel(); return Task.CompletedTask;
            }, clock);
            completed = (await recovery.RecoverRootAsync(original.Witness.RootId, cancellation.Token))!;
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(original with { AcknowledgedAt = clock.Now }, completed);
            Assert.IsNull(await recovery.RecoverRootAsync(original.Witness.RootId));
            Assert.AreEqual(1, calls); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.AreEqual(enabled, (await catalog.ReadAdapterTopologyAsync()).Instances.Single().Enabled);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await reopened.ReadRootLabelJournalAcknowledgementAsync(completed.Observation.OperationId));
        Assert.IsNull(await reopened.ReadPendingRootLabelJournalAcknowledgementAsync(completed.Witness.RootId));
    }

    [TestMethod]
    public async Task RootLabelJournalReplayLostCatalogCommitReinvokesOnlyTheSameOriginalAndKeepsItsFirstStart()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        var calls = new List<Guid>();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, async (id, token) =>
        {
            actor.AssertAccess(); calls.Add(id);
            if (calls.Count == 1)
                await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER reject_root_label_ack BEFORE UPDATE ON root_label_journal_acknowledgements BEGIN SELECT RAISE(ABORT,'simulated receipt write failure'); END;");
        }, clock);
        await Assert.ThrowsExactlyAsync<SqliteException>(async () => await recovery.RecoverRootAsync(original.Witness.RootId));
        Assert.AreEqual(original, await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(original.Witness.RootId));
        CollectionAssert.AreEqual(new[] { original.Witness.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_root_label_ack;");
        clock.Now = clock.Now.AddSeconds(1);
        var completed = await recovery.RecoverRootAsync(original.Witness.RootId);
        Assert.AreEqual(original with { AcknowledgedAt = clock.Now }, completed);
        CollectionAssert.AreEqual(new[] { original.Observation.OperationId, original.Observation.OperationId }, calls);
    }

    [TestMethod]
    public async Task RootLabelJournalReplayClassifiesOnlyAnAlreadyOwnedOriginalAndRetainsItsFirstCompletedReceipt()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        var calls = new List<Guid>();
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, (id, token) =>
        {
            actor.AssertAccess(); calls.Add(id); return Task.CompletedTask;
        }, clock);
        Assert.IsFalse(await recovery.TryReplayOwnedAsync(Guid.NewGuid()));
        Assert.IsEmpty(calls);
        Assert.IsTrue(await recovery.TryReplayOwnedAsync(original.Observation.OperationId));
        var first = await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId);
        clock.Now = clock.Now.AddDays(1);
        Assert.IsTrue(await recovery.TryReplayOwnedAsync(original.Observation.OperationId));
        Assert.AreEqual(original with { AcknowledgedAt = clock.Now.AddDays(-1) }, first);
        Assert.AreEqual(first, await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
        CollectionAssert.AreEqual(new[] { original.Observation.OperationId, original.Observation.OperationId }, calls);
        Assert.IsNull(await recovery.RecoverRootAsync(original.Witness.RootId));
    }

    [TestMethod]
    public async Task RootLabelJournalReplaySerializesActualReturnsWithTheSharedActorAndNeverReplacesTheFirstReceipt()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var actor = new MirrorPulseNamespaceActor();
        var original = await PrepareRootLabelJournalStartAsync(catalog);
        var clock = new RootLabelReplayClock(original.StartedAt.AddSeconds(1));
        var firstTime = clock.Now;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var recovery = new MirrorPulseRootLabelJournalRecovery(catalog, actor, async (id, token) =>
        {
            actor.AssertAccess(); Assert.AreEqual(original.Observation.OperationId, id);
            if (++calls == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            else clock.Now = firstTime.AddSeconds(1);
        }, clock);
        Task<bool> first = recovery.TryReplayOwnedAsync(original.Observation.OperationId).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> second;
        try
        {
            second = recovery.TryReplayOwnedAsync(original.Observation.OperationId).AsTask();
            Assert.IsFalse(second.IsCompleted); Assert.AreEqual(1, calls);
        }
        finally { release.TrySetResult(); }
        Assert.IsTrue(await first); Assert.IsTrue(await second); Assert.AreEqual(2, calls);
        Assert.AreEqual(original with { AcknowledgedAt = firstTime },
            await catalog.ReadRootLabelJournalAcknowledgementAsync(original.Observation.OperationId));
    }

    private static async Task<MirrorPulseRootLabelJournalAcknowledgement> PrepareRootLabelReplayStartAsync(
        MirrorPulseProductCatalog catalog, bool enabled)
    {
        var setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
        var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
        var audit = receipt.Root.VerifiedAt.AddSeconds(3);
        return await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
            audit, RootLabelNativeIdentity(setup, audit), RootLabelObservation(setup, audit), audit.AddSeconds(1));
    }

    private sealed class RootLabelReplayClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
