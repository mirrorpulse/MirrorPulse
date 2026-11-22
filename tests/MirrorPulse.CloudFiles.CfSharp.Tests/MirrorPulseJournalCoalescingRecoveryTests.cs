using System.Security.Cryptography;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalCoalescingRecoveryTests
{
    [TestMethod]
    [DataRow("unfinished")]
    [DataRow("ambiguous")]
    [DataRow("plan")]
    [DataRow("authorization")]
    [DataRow("plan-json")]
    [DataRow("authorization-json")]
    [DataRow("missing-plan")]
    public async Task InvalidGroupRetainsItsCommandAndFaultWhileAnIndependentGroupCompletes(string scenario)
    {
        await using var fixture = await Fixture.OpenAsync();
        Group[] groups = [await fixture.PrepareAsync(), await fixture.PrepareAsync()];
        groups = groups.OrderBy(group => group.Plan.PlanId.ToString("D"), StringComparer.Ordinal).ToArray();
        Group failed = groups[0], peer = groups[1];
        await fixture.FinishAsync(peer);
        if (scenario == "ambiguous")
        {
            await fixture.Catalog.TransitionMutationAsync(failed.Effect, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await fixture.Catalog.TransitionMutationAsync(failed.Effect, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Ambiguous);
        }
        if (scenario is not ("unfinished" or "ambiguous")) await fixture.ChangeEvidenceAsync(failed, scenario, restore: false);

        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(failed.Command))!.State);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(peer.Command))!.State);
        Assert.AreEqual(new MirrorPulseJournalPumpHealth(false, 1, "JournalGroupRecoveryFailed"), fixture.Runner.Health);
        string log = await File.ReadAllTextAsync(fixture.Log.FilePath);
        StringAssert.Contains(log, failed.Plan.PlanId.ToString("D"));
        StringAssert.Contains(log, "JournalGroupRecoveryFailed");
        Assert.IsFalse(log.Contains(failed.Plan.OriginalPath, StringComparison.Ordinal));

        // A normal source read must not erase a separate group recovery fault.
        Assert.IsFalse(await fixture.Runner.RunCycleAsync(_ => ValueTask.FromResult(new MirrorPulseJournalUploadBatch([], 0, false)),
            (_, _) => throw new AssertFailedException(), (_, _, _, _) => throw new AssertFailedException(), default));
        Assert.AreEqual(1, fixture.Runner.Health.PendingFaults);
        if (scenario is not ("unfinished" or "ambiguous")) await fixture.ChangeEvidenceAsync(failed, scenario, restore: true);
        await fixture.FinishAsync(failed);
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(failed.Command))!.State);
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task PendingOfficialOriginalPreventsCompletionEvenWhenEveryEffectIsAcknowledged()
    {
        await using var fixture = await Fixture.OpenAsync();
        Group group = await fixture.PrepareAsync();
        await fixture.FinishAsync(group);
        Guid original = group.Plan.Members[^1].OperationId;
        var entry = new CloudOperationJournalEntry(original, CloudStateOperationKind.MetadataUpdate, null, [1, 2], DateTimeOffset.UtcNow);
        await using (var transaction = await fixture.Store.BeginTransactionAsync())
        { await transaction.Operations.EnqueueAsync(entry); await transaction.CommitAsync(); }
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(group.Command))!.State);
        await using var reading = await fixture.Store.BeginTransactionAsync();
        var retained = (await reading.Operations.GetAsync(original))!;
        Assert.IsNotNull(retained);
        Assert.AreEqual(0, retained.AttemptCount);
        CollectionAssert.AreEqual(entry.Payload.ToArray(), retained.Payload.ToArray());
        await reading.RollbackAsync();
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task MissingOfficialOriginalsCannotResolveKeepLocalAfterOnlyItsMoveHasCompleted()
    {
        await using var fixture = await Fixture.OpenAsync();
        Group group = await fixture.PrepareAsync(moveContent: true);
        await fixture.FinishAsync(group);
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(group.Command))!.State);
        Assert.IsNull(await fixture.Catalog.ReadMutationAsync(group.Followup!.Value));
        Assert.AreEqual(1, fixture.Runner.Health.PendingFaults);

        var upload = await fixture.Catalog.PrepareJournalCoalescingMutationAsync(group.Plan.PlanId, 1);
        Assert.AreEqual("accepted-move", upload.Intent.ExpectedRevision);
        Assert.AreEqual(group.Followup, upload.Intent.OperationId);
        await fixture.FinishAsync(group with { Effect = upload.Intent.OperationId });
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(group.Command))!.State);
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task RecoveryBudgetRotatesPastSixtyFourPendingGroupsAndDoesNotAcknowledgeTheirOriginals()
    {
        await using var fixture = await Fixture.OpenAsync();
        var groups = new List<Group>();
        for (int index = 0; index < 65; index++) groups.Add(await fixture.PrepareAsync());
        groups.Sort((left, right) => StringComparer.Ordinal.Compare(left.Plan.PlanId.ToString("D"), right.Plan.PlanId.ToString("D")));
        Group last = groups[^1];
        foreach (Group pending in groups.Take(64))
        {
            await using var transaction = await fixture.Store.BeginTransactionAsync();
            await transaction.Operations.EnqueueAsync(new(pending.Plan.Members[0].OperationId,
                CloudStateOperationKind.MetadataUpdate, null, [3], DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        await fixture.FinishAsync(last);
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(last.Command))!.State);
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(last.Command))!.State);
        await using var reading = await fixture.Store.BeginTransactionAsync();
        foreach (Group pending in groups.Take(64))
        {
            Assert.IsNotNull(await reading.Operations.GetAsync(pending.Plan.Members[0].OperationId));
            Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(pending.Command))!.State);
        }
        await reading.RollbackAsync();
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task CancellationDoesNotBecomeAGroupFaultOrResolveACommand()
    {
        await using var fixture = await Fixture.OpenAsync();
        Group group = await fixture.PrepareAsync();
        await fixture.FinishAsync(group);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Recovery.RepairAsync(cancellation.Token).AsTask());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(group.Command))!.State);
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task FailedDiagnosticSinkDoesNotHideTheGroupFaultOrPreventPeerRecovery()
    {
        await using var fixture = await Fixture.OpenAsync();
        Group failed = await fixture.PrepareAsync(), peer = await fixture.PrepareAsync();
        await fixture.FinishAsync(peer); fixture.Log.Dispose();
        await fixture.Recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(failed.Command))!.State);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(peer.Command))!.State);
        Assert.AreEqual(1, fixture.Runner.Health.PendingFaults);
    }

    [TestMethod]
    public async Task UnavailableSharedOfficialStoreRemainsASourceFailureWithoutResolvingAnyGroup()
    {
        await using var fixture = await Fixture.OpenAsync();
        Group group = await fixture.PrepareAsync(); await fixture.FinishAsync(group);
        await fixture.Store.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Recovery.RepairAsync(default).AsTask());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(group.Command))!.State);
        Assert.IsTrue(fixture.Runner.Health.Healthy, "The pump runner records a shared source failure; it must not be mislabeled as a single group fault.");
    }

    private sealed record Group(MirrorPulseJournalCoalescingPlan Plan, Guid Command, Guid Effect, Guid? Followup = null);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-group-recovery", Guid.NewGuid().ToString("N"));
        private MirrorPulseStoragePaths Paths => new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal ICloudStateStore Store { get; private set; } = null!;
        internal MirrorPulseJournalPumpRunner Runner { get; } = new();
        internal LocalRollingLogWriter Log { get; private set; } = null!;
        internal MirrorPulseJournalCoalescingRecovery Recovery { get; private set; } = null!;
        private readonly Dictionary<Guid, object> _originalPayloads = [];

        internal static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.Paths.SyncRootPath);
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
            fixture.Store = await state.OpenAsync(new CloudStateStoreContext(fixture.Paths.SyncRootPath));
            fixture.Log = new(Path.Combine(fixture.Paths.DataRootPath, "logs"));
            fixture.Recovery = new(fixture.Catalog, state, fixture.Runner, fixture.Log);
            return fixture;
        }

        internal async Task<Group> PrepareAsync(bool moveContent = false)
        {
            InstanceId instance = InstanceId.New(); Guid item = Guid.NewGuid();
            string path = "private-" + Guid.NewGuid().ToString("N") + ".txt";
            MirrorPulseWorkerChangeCommand[] window = moveContent ?
                [new(Guid.NewGuid(), 1, instance, "files", MirrorPulseWorkerChangeKind.Move, "middle-" + path, "files", path, false, item, DateTimeOffset.UtcNow),
                 new(Guid.NewGuid(), 2, instance, "files", MirrorPulseWorkerChangeKind.Move, "final-" + path, "files", "middle-" + path, false, item, DateTimeOffset.UtcNow)] :
                [new(Guid.NewGuid(), 1, instance, "files", MirrorPulseWorkerChangeKind.Create,
                 path, null, null, false, item, DateTimeOffset.UtcNow), new(Guid.NewGuid(), 2, instance, "files", MirrorPulseWorkerChangeKind.Delete,
                 path, null, null, false, item, DateTimeOffset.UtcNow)];
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
            await Catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, moveContent ? "baseline" : null);
            var rejected = await Catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
            await Catalog.TransitionMutationAsync(rejected.Intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await Catalog.TransitionMutationAsync(rejected.Intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
            Guid conflict = Guid.Empty;
            foreach (var member in plan.Members)
            {
                conflict = Guid.NewGuid();
                await Catalog.SaveUploadConflictAsync(new(conflict, instance, member.OperationId.ToString("D"), path,
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, null, "observed", DateTimeOffset.UtcNow));
            }
            Guid command = Guid.NewGuid(); await Catalog.SaveUserCommandAsync(new(command, moveContent ? "keeplocal" : "retry", conflict.ToString("D"), "pending"));
            var retry = moveContent ? await Catalog.AuthorizeJournalCoalescingKeepLocalContentAsync(command, conflict,
                new(1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })), new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.None))) :
                await Catalog.AuthorizeJournalCoalescingRetryAsync(command, conflict, false);
            return new(plan, command, retry.Intent.OperationId, retry.FollowupOperationId);
        }

        // Synthetic acceptance is limited to this product-projection fixture;
        // native confirmation and real remote acceptance belong to the native suite.
        internal async Task FinishAsync(Group group)
        {
            var record = (await Catalog.ReadMutationAsync(group.Effect))!;
            if (record.State == MirrorPulseMutationState.Prepared)
                await Catalog.TransitionMutationAsync(group.Effect, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await Catalog.TransitionMutationAsync(group.Effect, record.State == MirrorPulseMutationState.Ambiguous
                ? MirrorPulseMutationState.Ambiguous : MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted,
                record.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? "accepted-move" : record.Intent.ContentSha256);
            await Catalog.TransitionMutationAsync(group.Effect, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged);
        }

        internal async Task ChangeEvidenceAsync(Group group, string scenario, bool restore)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false, ForeignKeys = false }.ToString());
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            bool plan = scenario is "plan" or "plan-json" or "missing-plan";
            string table = plan ? "journal_coalescing_plans" : "journal_coalescing_retries";
            query.CommandText = $"SELECT payload FROM {table} WHERE plan_id=$plan;";
            query.Parameters.AddWithValue("$plan", group.Plan.PlanId.ToString("D"));
            if (!restore) _originalPayloads[group.Plan.PlanId] = (await query.ExecuteScalarAsync())!;
            object original = _originalPayloads[group.Plan.PlanId];
            if (scenario == "missing-plan")
            {
                query.CommandText = restore ? "INSERT INTO journal_coalescing_plans(plan_id,payload) VALUES($plan,$payload);"
                    : "DELETE FROM journal_coalescing_plans WHERE plan_id=$plan;";
                query.Parameters.AddWithValue("$payload", original);
            }
            else if (plan)
            {
                query.CommandText = "UPDATE journal_coalescing_plans SET payload=$payload WHERE plan_id=$plan;";
                query.Parameters.AddWithValue("$payload", restore ? original : scenario == "plan-json" ? "{" : "{}");
            }
            else
            {
                byte[] payload = !restore && scenario == "authorization-json" ? "{"u8.ToArray() : (byte[])original;
                query.CommandText = "UPDATE journal_coalescing_retries SET payload=$payload,fingerprint=$hash WHERE plan_id=$plan;";
                query.Parameters.AddWithValue("$payload", payload);
                query.Parameters.AddWithValue("$hash", restore || scenario == "authorization-json" ? SHA256.HashData(payload) : new byte[32]);
            }
            await query.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Log.Dispose(); await Store.DisposeAsync(); await Catalog.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
