using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task TransferAdmissionRetainsItsOriginalClaimAndBothFencesTogetherAcrossOwners(bool sameInstance, bool empty)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceTransferPlan plan;
        MirrorPulseNamespaceTransferJournalClaim claim;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog, sameInstance)).Plan);
            claim = TransferJournalClaim(plan);
            if (empty) claim = claim with { ThroughSequence = 0, Members = [] };
            var actual = await catalog.PrepareNamespaceTransferWithJournalAsync(plan, claim);
            Assert.AreEqual(plan, actual.Plan);
            Assert.AreEqual(claim, actual.JournalClaim);
            Assert.IsNull(actual.CopyStartedAt);
            Assert.IsNull(actual.Acknowledgement);
            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
            foreach (var member in claim.Members)
                Assert.AreEqual(plan.OperationId, await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.ReadNamespaceTransferAsync(plan.OperationId);
        Assert.AreEqual(claim, retained!.JournalClaim);
        Assert.AreEqual(retained, await reopened.PrepareNamespaceTransferWithJournalAsync(plan, claim));
        Assert.IsNull(retained.CopyStartedAt);
        Assert.IsNull(retained.Acknowledgement);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    [DataRow("mutation")]
    [DataRow("coalescing")]
    public async Task PreviouslyOwnedOriginalRejectsTheEntireNewTransferWithoutPublishingEitherFence(string owner)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        var claim = TransferJournalClaim(plan);
        Guid originalId = claim.Members[^1].OperationId;
        var mutation = TransferJournalMutation(plan, originalId);
        var coalescing = TransferJournalCoalescing(plan, claim.Members.Select(member => member.OperationId).ToArray());
        if (owner == "mutation") await catalog.PrepareMutationAsync(mutation);
        else await catalog.PrepareJournalCoalescingAsync(coalescing);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferWithJournalAsync(plan, claim));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        if (owner == "mutation") Assert.AreEqual(mutation, (await catalog.ReadMutationAsync(originalId))!.Intent);
        else Assert.AreEqual(JsonSerializer.Serialize(coalescing), JsonSerializer.Serialize(await catalog.ReadJournalCoalescingOwnerAsync(originalId)));
        Assert.AreEqual(plan.Source, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.Source.Context.Member.MemberId));
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    [DataRow("payload")]
    [DataRow("second-root")]
    [DataRow("effect")]
    [DataRow("last-journal")]
    public async Task InterruptedAtomicTransferAdmissionRollsBackItsPlanEffectsOriginalsAndBothFences(string cut)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceTransferPlan plan;
        MirrorPulseNamespaceTransferJournalClaim claim;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
            claim = TransferJournalClaim(plan);
            string target = cut switch
            {
                "payload" => "BEFORE INSERT ON namespace_transfers",
                "second-root" => $"BEFORE INSERT ON namespace_transfer_roots WHEN NEW.root_id='{plan.TargetRoute.RootId}'",
                "effect" => "BEFORE INSERT ON namespace_transfer_effects WHEN NEW.kind=1",
                _ => "BEFORE INSERT ON namespace_transfer_journal_members WHEN NEW.sequence=12"
            };
            await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_atomic_transfer {target} BEGIN SELECT RAISE(ABORT,'original atomic admission interruption'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceTransferWithJournalAsync(plan, claim));
            Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
            await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT (SELECT COUNT(*) FROM namespace_transfers)+(SELECT COUNT(*) FROM namespace_transfer_states)+(SELECT COUNT(*) FROM namespace_transfer_roots)+(SELECT COUNT(*) FROM namespace_transfer_effects)+(SELECT COUNT(*) FROM namespace_transfer_journal_members);";
            Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_atomic_transfer;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var admitted = await reopened.PrepareNamespaceTransferWithJournalAsync(plan, claim);
        Assert.AreEqual(claim, admitted.JournalClaim);
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AtomicAdmissionReplayCannotInventAMissingOriginalClaimOnAnExistingTransfer(bool started)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        await catalog.PrepareNamespaceTransferAsync(plan);
        if (started) await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(2));
        string original = await ReadTransferPayloadHexAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferWithJournalAsync(plan, TransferJournalClaim(plan)));
        Assert.AreEqual(original, await ReadTransferPayloadHexAsync(fixture.Paths));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim);
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    public async Task CancelledAtomicTransferAdmissionPublishesNoOriginalOwnerOrRootFence()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        var claim = TransferJournalClaim(plan);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareNamespaceTransferWithJournalAsync(plan, claim, cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        foreach (var member in claim.Members) Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    public async Task AtomicTransferAdmissionCopiesCallerObservationsAndRejectsAnAlteredReplay()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(catalog)).Plan);
        var original = TransferJournalClaim(plan);
        var callerMembers = original.Members.ToArray();
        var callerClaim = original with { Members = callerMembers };
        var admitted = await catalog.PrepareNamespaceTransferWithJournalAsync(plan, callerClaim);
        callerMembers[0] = callerMembers[0] with { OperationId = Guid.NewGuid() };
        Assert.AreEqual(original, admitted.JournalClaim);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferWithJournalAsync(plan, callerClaim));
        Assert.AreEqual(original, (await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.JournalClaim);
        Assert.IsNull(await catalog.ReadNamespaceTransferJournalOwnerAsync(callerMembers[0].OperationId));
        Assert.AreEqual(plan.OperationId, await catalog.ReadNamespaceTransferJournalOwnerAsync(original.Members[0].OperationId));
    }

    [TestMethod]
    [TestCategory("AtomicTransferJournalAdmission")]
    public async Task RacingMutationAndAtomicTransferRetainExactlyOneOriginalOwnerInTheExclusiveCatalog()
    {
        using var fixture = new Fixture();
        await using var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = WithTransferCommandOrigin((await PrepareTransferFixtureAsync(first)).Plan);
        var claim = TransferJournalClaim(plan);
        Guid original = claim.Members[^1].OperationId;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = Task.Run(async () => { await release.Task; return await AttemptAsync(async () => { await first.PrepareNamespaceTransferWithJournalAsync(plan, claim); }); });
        var mutation = Task.Run(async () => { await release.Task; return await AttemptAsync(async () => { await first.PrepareMutationAsync(TransferJournalMutation(plan, original)); }); });
        release.SetResult();
        var results = await Task.WhenAll(transfer, mutation);
        Assert.AreEqual(1, results.Count(result => result));
        if (results[0])
        {
            Assert.IsNull(await first.ReadMutationAsync(original));
            Assert.AreEqual(plan.OperationId, await first.ReadNamespaceTransferJournalOwnerAsync(original));
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        else
        {
            Assert.IsNotNull(await first.ReadMutationAsync(original));
            Assert.IsNull(await first.ReadNamespaceTransferAsync(plan.OperationId));
            Assert.IsEmpty(await first.ReadNamespaceDispatchFencedRootsAsync());
            foreach (var member in claim.Members) Assert.IsNull(await first.ReadNamespaceTransferJournalOwnerAsync(member.OperationId));
        }
        // The product has exactly one Host owner. A separate read-only connection
        // independently inspects the committed competing ownership indexes.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT (SELECT COUNT(*) FROM mutation_intents WHERE operation_id=$id)+(SELECT COUNT(*) FROM namespace_transfer_journal_members WHERE operation_id=$id);";
        query.Parameters.AddWithValue("$id", original.ToString("D"));
        Assert.AreEqual(1L, await query.ExecuteScalarAsync());
        static async Task<bool> AttemptAsync(Func<Task> operation)
        {
            try { await operation(); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
