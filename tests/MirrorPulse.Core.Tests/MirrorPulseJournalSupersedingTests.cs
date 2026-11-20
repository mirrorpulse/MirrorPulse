using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalSupersedingTests
{
    [TestMethod]
    public async Task SupersedingRetainsOriginalBindingAndRevisionAcrossRestartWithoutIndividualDispatchOrAcknowledgement()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
                await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            foreach (MirrorPulseMutationIntent intent in intents)
            {
                MirrorPulseMutationRecord restored = (await reopened.ReadMutationAsync(intent.OperationId))!;
                Assert.AreEqual(intent, restored.Intent);
                Assert.AreEqual(MirrorPulseMutationState.Superseded, restored.State);
                Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, restored.ExecutionEvidence);
                Assert.AreEqual(plan.PlanId, restored.SupersededByPlanId);
                Assert.IsNull(restored.AcceptedRevision);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MirrorPulseMutationExecutor(reopened).ExecuteAsync(intent,
                    _ => throw new AssertFailedException("A superseded original cannot dispatch."),
                    (_, _) => throw new AssertFailedException("Superseding is not acknowledgement."), default).AsTask());
                await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => new MirrorPulseMutationExecutor(reopened).ReconcileAsync(restored,
                    (_, _) => throw new AssertFailedException("The aggregate owner must perform verification."),
                    (_, _) => throw new AssertFailedException("The original cannot acknowledge alone."), default).AsTask());
            }
            Assert.IsEmpty(await reopened.ReadIncompleteMutationsAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareMutationAsync(intents[0] with { OperationId = plan.PlanId }));
            Assert.IsNull(await reopened.ReadMutationAsync(plan.PlanId));
            await reopened.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, [intents[0]]));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window,
                [intents[0] with { ExpectedRevision = "invented-baseline" }, intents[1]]));
            MirrorPulseJournalCoalescingPlan retained = (await reopened.ReadJournalCoalescingPlansAsync()).Single();
            CollectionAssert.AreEqual(plan.Members.ToArray(), retained.Members.ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Conflict)]
    [DataRow(MirrorPulseMutationState.Acknowledged)]
    public async Task AnyStartedSiblingPreventsSupersedingAndLeavesTheEarlierIntentUnreserved(MirrorPulseMutationState state)
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
            Guid operation = intents[1].OperationId;
            await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            if (state == MirrorPulseMutationState.Acknowledged)
            {
                await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
                await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.RemoteAccepted, state);
            }
            else if (state != MirrorPulseMutationState.Executing)
                await catalog.TransitionMutationAsync(operation, MirrorPulseMutationState.Executing, state);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
            Assert.AreEqual(state, (await catalog.ReadMutationAsync(operation))!.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, (await catalog.ReadMutationAsync(operation))!.ExecutionEvidence);
            Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(intents[0].OperationId))!.State);
            await catalog.PrepareMutationAsync(intents[0]);
            await catalog.TransitionMutationAsync(intents[0].OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task LegacyUnknownPreparedSiblingCannotBeAdoptedAsNeverSent()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await catalog.PrepareMutationAsync(intents[1]);
            await ExecuteSqlAsync(paths, "ALTER TABLE mutation_intents DROP COLUMN execution_started; PRAGMA user_version=19;");
            await using var migrated = await MirrorPulseProductCatalog.OpenAsync(paths);
            await migrated.PrepareMutationAsync(intents[0]);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => migrated.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Unknown, (await migrated.ReadMutationAsync(intents[1].OperationId))!.ExecutionEvidence);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, (await migrated.ReadMutationAsync(intents[0].OperationId))!.ExecutionEvidence);
            Assert.IsEmpty(await migrated.ReadJournalCoalescingPlansAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("window")]
    [DataRow("revision")]
    [DataRow("binding")]
    public async Task ChangedObservationOrHistoricalIntentCannotTransferOwnership(string changed)
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        MirrorPulseMutationIntent original = intents[0];
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
            if (changed == "window") window[1] = window[1] with { ObservedAt = window[1].ObservedAt.AddTicks(1) };
            if (changed == "revision") intents[0] = original with { ExpectedRevision = "newer-remote-version" };
            if (changed == "binding") intents[0] = original with
            {
                UploadBinding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile),
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            Assert.AreEqual(original, (await catalog.ReadMutationAsync(original.OperationId))!.Intent);
            Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(original.OperationId))!.State);
            Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("object")]
    [DataRow("baseline")]
    [DataRow("missing-proof")]
    public async Task PreparedInputsWithDifferentObjectsOrBaselinesOrMissingProofCannotMerge(string boundary)
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        if (boundary == "object") intents[1] = intents[1] with
        {
            UploadBinding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile),
        };
        if (boundary == "baseline") intents[1] = intents[1] with { ExpectedRevision = "different-baseline" };
        if (boundary == "missing-proof") intents[1] = intents[1] with { UploadBinding = null };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
            foreach (MirrorPulseMutationIntent intent in intents)
            {
                MirrorPulseMutationRecord unchanged = (await catalog.ReadMutationAsync(intent.OperationId))!;
                Assert.AreEqual(intent, unchanged.Intent);
                Assert.AreEqual(MirrorPulseMutationState.Prepared, unchanged.State);
                Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, unchanged.ExecutionEvidence);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task FailureAfterSupersedingButBeforeLastMemberInsertionRollsBackTheEntireOwnershipTransfer()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
            await ExecuteSqlAsync(paths, $"""
                CREATE TRIGGER fail_last_coalescing_member BEFORE INSERT ON journal_coalescing_members
                WHEN NEW.operation_id='{intents[1].OperationId:D}' BEGIN SELECT RAISE(ABORT,'Injected member-write failure'); END;
                """);
            await using (var failing = await MirrorPulseProductCatalog.OpenAsync(paths))
                await Assert.ThrowsExactlyAsync<SqliteException>(() => failing.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.IsEmpty(await reopened.ReadJournalCoalescingPlansAsync());
            Assert.HasCount(2, await reopened.ReadIncompleteMutationsAsync());
            foreach (MirrorPulseMutationIntent intent in intents)
            {
                MirrorPulseMutationRecord record = await reopened.PrepareMutationAsync(intent);
                Assert.AreEqual(MirrorPulseMutationState.Prepared, record.State);
                Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, record.ExecutionEvidence);
                Assert.IsNull(record.SupersededByPlanId);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CompetingExecutionAndSupersedingCanCommitOnlyOneOwner()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            foreach (MirrorPulseMutationIntent intent in intents) await catalog.PrepareMutationAsync(intent);
            async Task<bool> CommitAsync(bool superseding)
            {
                try
                {
                    if (superseding) await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents);
                    else await catalog.TransitionMutationAsync(intents[0].OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }
            bool[] committed = await Task.WhenAll(Task.Run(() => CommitAsync(true)), Task.Run(() => CommitAsync(false)));
            Assert.AreEqual(1, committed.Count(success => success));
            MirrorPulseMutationRecord record = (await catalog.ReadMutationAsync(intents[0].OperationId))!;
            Assert.AreEqual(committed[0] ? MirrorPulseMutationState.Superseded : MirrorPulseMutationState.Executing, record.State);
            Assert.AreEqual(committed[0] ? MirrorPulseMutationExecutionEvidence.NeverStarted : MirrorPulseMutationExecutionEvidence.Started, record.ExecutionEvidence);
            Assert.HasCount(committed[0] ? 1 : 0, await catalog.ReadJournalCoalescingPlansAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MissingOriginalIntentCannotBeInventedDuringSuperseding()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        var (window, plan, intents) = Chain();
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.PrepareMutationAsync(intents[0]);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(plan, window, intents));
            Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
            await catalog.PrepareMutationAsync(intents[1]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static (MirrorPulseWorkerChangeCommand[] Window, MirrorPulseJournalCoalescingPlan Plan, MirrorPulseMutationIntent[] Intents) Chain()
    {
        InstanceId instance = InstanceId.New();
        Guid item = Guid.NewGuid();
        var first = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, instance, "files", MirrorPulseWorkerChangeKind.ContentUpdate,
            "file.txt", null, null, false, item, DateTimeOffset.UtcNow);
        var second = first with { OperationId = Guid.NewGuid(), Sequence = 2, ObservedAt = first.ObservedAt.AddSeconds(1) };
        MirrorPulseWorkerChangeCommand[] window = [first, second];
        MirrorPulseJournalCoalescingPlan plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
        var binding = new MirrorPulseUploadBinding(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile);
        MirrorPulseMutationIntent Intent(MirrorPulseWorkerChangeCommand command) => new(command.OperationId, command.InstanceId, command.RootKey,
            command.Kind, command.RelativePath, null, false, "original-baseline", 4, new string('A', 64), MirrorPulseMutationOrigin.Journal,
            binding);
        return (window, plan, [Intent(first), Intent(second)]);
    }

    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.ProductCatalogDatabasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string TestDirectory() => Path.Combine(Path.GetTempPath(), "MirrorPulse-journal-superseding", Guid.NewGuid().ToString("N"));
    private static MirrorPulseStoragePaths Paths(string directory) => new(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
}
