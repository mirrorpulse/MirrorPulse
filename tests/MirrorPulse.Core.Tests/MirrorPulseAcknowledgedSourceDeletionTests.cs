using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseAcknowledgedSourceDeletionTests
{
    [TestMethod]
    [DataRow(MirrorPulseMutationState.Prepared, false)]
    [DataRow(MirrorPulseMutationState.Executing, false)]
    [DataRow(MirrorPulseMutationState.Ambiguous, false)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted, false)]
    [DataRow(MirrorPulseMutationState.Conflict, false)]
    [DataRow(MirrorPulseMutationState.Acknowledged, false)]
    [DataRow(MirrorPulseMutationState.Prepared, true)]
    [DataRow(MirrorPulseMutationState.Executing, true)]
    [DataRow(MirrorPulseMutationState.Ambiguous, true)]
    [DataRow(MirrorPulseMutationState.RemoteAccepted, true)]
    [DataRow(MirrorPulseMutationState.Conflict, true)]
    [DataRow(MirrorPulseMutationState.Acknowledged, true)]
    public async Task OnlyCompletedExactJournalGenerationSurvivesRestart(MirrorPulseMutationState outcome, bool directoryKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var intent = Delete() with { JournalItemId = Guid.NewGuid(), IsDirectory = directoryKind };
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.PrepareMutationAsync(intent);
                await ReachAsync(catalog, intent.OperationId, outcome);
                // A completed legacy Delete at the same path/revision does not identify this object.
                var legacy = intent with { OperationId = Guid.NewGuid(), JournalItemId = null };
                Assert.IsFalse(JsonSerializer.Serialize(legacy).Contains("JournalItemId", StringComparison.Ordinal));
                await catalog.PrepareMutationAsync(legacy);
                await ReachAsync(catalog, legacy.OperationId, MirrorPulseMutationState.Acknowledged);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareMutationAsync(legacy with { JournalItemId = Guid.NewGuid() }));
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var completed = await reopened.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { intent.JournalItemId!.Value });
            Assert.HasCount(outcome == MirrorPulseMutationState.Acknowledged ? 1 : 0, completed);
            if (completed.Count != 0)
                Assert.AreEqual(new MirrorPulseAcknowledgedSourceDeletion(intent.JournalItemId!.Value, intent.InstanceId,
                    intent.RootKey, intent.RelativePath, directoryKind, intent.ExpectedRevision!), completed.Single());
            Assert.IsEmpty(await reopened.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { Guid.NewGuid() }),
                "A later object at the same path cannot inherit an old deletion.");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareMutationAsync(intent with { JournalItemId = Guid.NewGuid() }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("rescan")]
    [DataRow("content")]
    public async Task InvalidJournalObjectCorrelationIsRejectedBeforeStorage(string invalid)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.DeleteFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var intent = Delete() with { JournalItemId = Guid.NewGuid() };
        intent = invalid switch
        {
            "empty" => intent with { JournalItemId = Guid.Empty },
            "rescan" => intent with { Origin = MirrorPulseMutationOrigin.Rescan },
            _ => intent with { Kind = MirrorPulseWorkerChangeKind.ContentUpdate },
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareMutationAsync(intent));
        Assert.IsNull(await catalog.ReadMutationAsync(intent.OperationId));
    }

    [TestMethod]
    public async Task CorruptedStoredJournalObjectCannotAuthorizeRescanSuppression()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.DeleteFile);
        var intent = Delete() with { JournalItemId = Guid.NewGuid() };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareMutationAsync(intent);
            await ReachAsync(catalog, intent.OperationId, MirrorPulseMutationState.Acknowledged);
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mutation_intents SET payload=CAST(json_set(CAST(payload AS TEXT),'$.JournalItemId',$empty) AS BLOB) WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$empty", Guid.Empty.ToString("D"));
            command.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { intent.JournalItemId!.Value }));
    }

    [TestMethod]
    public async Task CoalescedDeleteUsesItsImmutableObjectAndFinalLocalName()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.DeleteFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, "baseline");
        var effect = await catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0);
        Assert.IsEmpty(await catalog.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { fixture.Plan.ItemId }));
        await ReachAsync(catalog, effect.Intent.OperationId, MirrorPulseMutationState.RemoteAccepted);
        Assert.IsEmpty(await catalog.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { fixture.Plan.ItemId }));
        await catalog.TransitionMutationAsync(effect.Intent.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged);
        Assert.AreEqual(new MirrorPulseAcknowledgedSourceDeletion(fixture.Plan.ItemId, fixture.Plan.InstanceId,
            fixture.Plan.RootKey, fixture.Plan.FinalPath, false, "baseline"),
            (await catalog.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { fixture.Plan.ItemId })).Single());
        Assert.IsEmpty(await catalog.ReadAcknowledgedSourceDeletionsAsync(new HashSet<Guid> { Guid.NewGuid() }));
    }

    private static MirrorPulseMutationIntent Delete() => new(Guid.NewGuid(), InstanceId.New(), "docs",
        MirrorPulseWorkerChangeKind.Delete, "old.txt", null, false, "old-v1", null, null, MirrorPulseMutationOrigin.Journal);

    private static async Task ReachAsync(MirrorPulseProductCatalog catalog, Guid operationId, MirrorPulseMutationState outcome)
    {
        if (outcome == MirrorPulseMutationState.Prepared) return;
        await catalog.TransitionMutationAsync(operationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        if (outcome == MirrorPulseMutationState.Executing) return;
        await catalog.TransitionMutationAsync(operationId, MirrorPulseMutationState.Executing,
            outcome == MirrorPulseMutationState.Acknowledged ? MirrorPulseMutationState.RemoteAccepted : outcome);
        if (outcome == MirrorPulseMutationState.Acknowledged)
            await catalog.TransitionMutationAsync(operationId, MirrorPulseMutationState.RemoteAccepted, outcome);
    }
}
