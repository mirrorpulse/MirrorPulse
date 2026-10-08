using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseMutationExecutionEvidenceTests
{
    [TestMethod]
    [DataRow(MirrorPulseMutationState.Executing)]
    [DataRow(MirrorPulseMutationState.Prepared)]
    [DataRow(MirrorPulseMutationState.Ambiguous)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted)]
    [DataRow(MirrorPulseMutationState.Acknowledged)]
    [DataRow(MirrorPulseMutationState.Conflict)]
    public async Task ExecutionWitnessSurvivesEveryOutcomeAndCatalogRestart(MirrorPulseMutationState outcome)
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted,
                    (await catalog.PrepareMutationAsync(intent)).ExecutionEvidence);
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
                if (outcome == MirrorPulseMutationState.Acknowledged)
                {
                    await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
                    await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.RemoteAccepted, outcome);
                }
                else if (outcome != MirrorPulseMutationState.Executing)
                    await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, outcome);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord restored = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(outcome, restored.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, restored.ExecutionEvidence);
            Assert.AreEqual(restored, await reopened.PrepareMutationAsync(intent));
            if (outcome != MirrorPulseMutationState.Acknowledged)
                Assert.AreEqual(restored, (await reopened.ReadIncompleteMutationsAsync()).Single());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task WorkerDispatchSeesDurableWitnessEvenWhenUnsupportedOperationReturnsToPrepared()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent();
        int dispatches = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    async token =>
                    {
                        MirrorPulseMutationRecord inFlight = (await catalog.ReadMutationAsync(intent.OperationId, token))!;
                        Assert.AreEqual(MirrorPulseMutationState.Executing, inFlight.State);
                        Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, inFlight.ExecutionEvidence);
                        dispatches++;
                        throw new NotSupportedException("The Worker does not support this mutation.");
                    }, (_, _) => throw new AssertFailedException("An unsupported mutation cannot be acknowledged."), default).AsTask());
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.Prepared, record.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, record.ExecutionEvidence);
            Assert.AreEqual(1, dispatches);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CancellationAfterDispatchRetainsExecutionWitnessWithoutAcknowledgement()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => throw new OperationCanceledException(), (_, _) => throw new AssertFailedException("Cancellation cannot acknowledge."), default).AsTask());
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.Executing, record.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, record.ExecutionEvidence);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task FailedStateTransitionCannotInventAStartedWitness()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent();
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await catalog.PrepareMutationAsync(intent);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionMutationAsync(intent.OperationId,
                MirrorPulseMutationState.Executing, MirrorPulseMutationState.Prepared));
            MirrorPulseMutationRecord unchanged = (await catalog.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.Prepared, unchanged.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, unchanged.ExecutionEvidence);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareMutationAsync(intent with { RelativePath = "replacement.txt" }));
            Assert.AreEqual(unchanged, await catalog.ReadMutationAsync(intent.OperationId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task LegacyPreparedMigrationRemainsUnknownUntilExecutionActuallyStarts()
    {
        string directory = TestDirectory();
        MirrorPulseStoragePaths paths = Paths(directory);
        MirrorPulseMutationIntent intent = MirrorPulseMutationExecutorTests.Intent();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await catalog.PrepareMutationAsync(intent);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.ProductCatalogDatabasePath,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand downgrade = connection.CreateCommand();
                downgrade.CommandText = "ALTER TABLE mutation_intents DROP COLUMN execution_started; PRAGMA user_version=19;";
                await downgrade.ExecuteNonQueryAsync();
            }
            await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseMutationRecord unknown = (await migrated.ReadMutationAsync(intent.OperationId))!;
                Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Unknown, unknown.ExecutionEvidence);
                Assert.AreEqual(unknown, await migrated.PrepareMutationAsync(intent));
                Assert.AreEqual(unknown, (await migrated.ReadIncompleteMutationsAsync()).Single());
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Unknown,
                (await reopened.ReadMutationAsync(intent.OperationId))!.ExecutionEvidence);
            await reopened.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await reopened.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Prepared);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started,
                (await reopened.ReadMutationAsync(intent.OperationId))!.ExecutionEvidence);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string TestDirectory() => Path.Combine(Path.GetTempPath(), "MirrorPulse-execution-evidence", Guid.NewGuid().ToString("N"));
    private static MirrorPulseStoragePaths Paths(string directory) => new(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
}
