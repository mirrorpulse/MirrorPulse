using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CfSharp;
using CfSharp.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalCoalescingExecutionTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task EveryEffectRetainsFinalProofAndSeparateRemoteIdentitiesAcrossRestart(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect);
        MirrorPulseJournalCoalescingExecution retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
            retained = await PrepareAsync(catalog, fixture);
            Assert.HasCount(effect is MirrorPulseCoalescedEffect.MoveAndUpdateFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? 2 : 1, retained.Steps);
            Assert.AreEqual(fixture.Content, retained.Content);
            string? expectedRevision = fixture.Revision;
            Assert.AreEqual(expectedRevision, retained.ExpectedRevision);
            Assert.IsEmpty(retained.OriginalMutations);
            foreach (var step in retained.Steps)
            {
                Assert.AreNotEqual(fixture.Plan.PlanId, step.OperationId);
                Assert.IsFalse(fixture.Plan.Members.Any(member => member.OperationId == step.OperationId));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(fixture.Intent(fixture.Window[0]) with { OperationId = step.OperationId }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareJournalCoalescingAsync(fixture.Plan with { PlanId = step.OperationId }));
            }
            if (effect == MirrorPulseCoalescedEffect.DeleteFile)
            {
                Assert.AreEqual("original.txt", retained.Steps[0].RelativePath);
                Assert.AreEqual(MirrorPulseCoalescingStepKind.Delete, retained.Steps[0].Kind);
            }
            if (effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile)
            {
                Assert.AreEqual(MirrorPulseCoalescingStepKind.Move, retained.Steps[0].Kind);
                Assert.AreEqual(MirrorPulseCoalescingStepKind.Upload, retained.Steps[1].Kind);
                Assert.AreEqual("original.txt", retained.Steps[0].PreviousRelativePath);
                Assert.AreEqual("final.txt", retained.Steps[1].RelativePath);
            }
            Assert.IsEmpty(await catalog.ReadIncompleteMutationsAsync());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var restored = (await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
        Assert.AreEqual(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(restored));
        Assert.AreEqual(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(await PrepareAsync(reopened, fixture)));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<MirrorPulseCoalescingStep>)restored.Steps).Add(retained.Steps[0]));
        foreach (var member in fixture.Plan.Members)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareMutationAsync(fixture.Intent(fixture.Window.Single(command => command.OperationId == member.OperationId))));
    }

    [TestMethod]
    public async Task SupersededOriginalIntentFingerprintAndBindingAreRetainedWithoutAdoptingCurrentObjects()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var intent = fixture.Intent(fixture.Window[0]) with { ContentLength = 1, ContentSha256 = new string('B', 64) };
        await catalog.PrepareMutationAsync(intent);
        await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(fixture.Plan, fixture.Window, [intent]);
        var execution = await PrepareAsync(catalog, fixture);
        Assert.HasCount(1, execution.OriginalMutations);
        Assert.AreEqual(intent.OperationId, execution.OriginalMutations[0].OperationId);
        Assert.AreEqual(64, execution.OriginalMutations[0].IntentFingerprint.Length);
        var original = (await catalog.ReadMutationAsync(intent.OperationId))!;
        Assert.AreEqual(intent, original.Intent);
        Assert.AreEqual(MirrorPulseMutationState.Superseded, original.State);
        Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, original.ExecutionEvidence);
        var different = fixture.Content! with
        {
            UploadBinding = fixture.Content!.UploadBinding with
            {
                LocalObject = fixture.Content.UploadBinding.LocalObject with { LocalFileId = Guid.NewGuid() },
            },
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan,
            fixture.Window, fixture.Revision, different));
        Assert.AreEqual(original, await catalog.ReadMutationAsync(intent.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(execution), JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    public async Task HistoricalRemoteBaselineCannotBeChangedBeforeExecutionPreparation()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var intent = fixture.Intent(fixture.Window[0]);
        await catalog.PrepareMutationAsync(intent);
        await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(fixture.Plan, fixture.Window, [intent]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan,
            fixture.Window, "later-remote-revision", fixture.Content));
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        foreach (var step in MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content).Steps)
            await catalog.PrepareMutationAsync(intent with { OperationId = step.OperationId });
    }

    [TestMethod]
    [DataRow("revision")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("binding")]
    public async Task PreparedExecutionCannotAdoptAnotherBaselineOrFinalContent(string field)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
        var retained = await PrepareAsync(catalog, fixture);
        var changed = field switch
        {
            "length" => fixture.Content! with { Length = fixture.Content!.Length + 1 },
            "hash" => fixture.Content! with { Sha256 = new string('B', 64) },
            "binding" => fixture.Content! with { UploadBinding = fixture.Content!.UploadBinding with { LocalObject = fixture.Content.UploadBinding.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            _ => fixture.Content,
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan,
            fixture.Window, field == "revision" ? "other" : fixture.Revision, changed));
        Assert.AreEqual(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
    }

    [TestMethod]
    public async Task LaterEffectIdentityCollisionLeavesNoPartialProgramOrEarlierReservation()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
        var execution = MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var collision = fixture.Intent(fixture.Window[0]) with { OperationId = execution.Steps[1].OperationId };
        await catalog.PrepareMutationAsync(collision);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PrepareAsync(catalog, fixture));
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        Assert.AreEqual(collision, (await catalog.ReadMutationAsync(collision.OperationId))!.Intent);
        await catalog.PrepareMutationAsync(collision with { OperationId = execution.Steps[0].OperationId });
    }

    [TestMethod]
    public async Task CompetingMutationAndProgramCannotBothOwnTheSameRemoteEffectIdentity()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
        var execution = MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> OwnAsync(bool program)
        {
            await start.Task;
            try
            {
                if (program) await PrepareAsync(catalog, fixture);
                else await catalog.PrepareMutationAsync(fixture.Intent(fixture.Window[0]) with { OperationId = execution.Steps[0].OperationId });
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var program = Task.Run(() => OwnAsync(true));
        var mutation = Task.Run(() => OwnAsync(false));
        start.SetResult();
        bool[] results = await Task.WhenAll(program, mutation);
        Assert.AreEqual(1, results.Count(result => result));
        Assert.AreEqual(results[0], await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId) is not null);
        Assert.AreEqual(results[1], await catalog.ReadMutationAsync(execution.Steps[0].OperationId) is not null);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("collision")]
    [DataRow("effect-id")]
    public async Task ExecutionRequiresTheCompleteWindowIncludingOtherObjectsAndIdentityOwnership(string field)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
        var blueprint = MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
        var other = fixture.Window[0] with
        {
            OperationId = field == "effect-id" ? blueprint.Steps[0].OperationId : Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Sequence = 99,
            RelativePath = field == "collision" ? "original.txt" : "unrelated.txt",
        };
        MirrorPulseWorkerChangeCommand[] window = field == "missing" ? fixture.Window[..1] : [.. fixture.Window, other];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan,
            window, fixture.Revision, fixture.Content));
        Assert.IsNull(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("version")]
    [DataRow("effect-index")]
    [DataRow("original-index")]
    [DataRow("plan")]
    [DataRow("original-intent")]
    public async Task CorruptedPreparationOrOwnershipCannotAuthorizeRecoveryAfterRestart(string field)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var original = fixture.Intent(fixture.Window[0]);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareMutationAsync(original);
            await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(fixture.Plan, fixture.Window, [original]);
            var execution = await PrepareAsync(catalog, fixture);
            Assert.AreEqual(MirrorPulseMutationState.Superseded, (await catalog.ReadMutationAsync(original.OperationId))!.State);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var change = connection.CreateCommand();
            change.Parameters.AddWithValue("$plan", fixture.Plan.PlanId.ToString("D"));
            change.CommandText = field switch
            {
                "fingerprint" => "UPDATE journal_coalescing_executions SET fingerprint=X'00' WHERE plan_id=$plan;",
                "effect-index" => "UPDATE journal_coalescing_steps SET operation_id=$other WHERE plan_id=$plan AND ordinal=0;",
                "original-index" => "DELETE FROM journal_coalescing_members WHERE plan_id=$plan AND operation_id=$original;",
                "plan" => "UPDATE journal_coalescing_plans SET payload=$payload WHERE plan_id=$plan;",
                "original-intent" => "UPDATE mutation_intents SET payload=$payload WHERE operation_id=$original;",
                _ => "UPDATE journal_coalescing_executions SET payload=$payload,fingerprint=$fingerprint WHERE plan_id=$plan;",
            };
            change.Parameters.AddWithValue("$other", Guid.NewGuid().ToString("D"));
            change.Parameters.AddWithValue("$original", original.OperationId.ToString("D"));
            if (field == "version")
            {
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT payload FROM journal_coalescing_executions WHERE plan_id=$plan;";
                read.Parameters.AddWithValue("$plan", fixture.Plan.PlanId.ToString("D"));
                var json = JsonNode.Parse((byte[])(await read.ExecuteScalarAsync())!)!;
                json["Version"] = 2;
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(json);
                change.Parameters.AddWithValue("$payload", payload);
                change.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            }
            else if (field is "plan" or "original-intent")
            {
                await using var read = connection.CreateCommand();
                read.CommandText = field == "plan" ? "SELECT payload FROM journal_coalescing_plans WHERE plan_id=$plan;"
                    : "SELECT payload FROM mutation_intents WHERE operation_id=$original;";
                read.Parameters.AddWithValue("$plan", fixture.Plan.PlanId.ToString("D"));
                read.Parameters.AddWithValue("$original", original.OperationId.ToString("D"));
                object stored = (await read.ExecuteScalarAsync())!;
                var json = stored is byte[] bytes ? JsonNode.Parse(bytes)! : JsonNode.Parse((string)stored)!;
                json[field == "plan" ? "FinalPath" : "RelativePath"] = "changed.txt";
                if (field == "plan") change.Parameters.AddWithValue("$payload", json.ToJsonString());
                else change.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(json));
            }
            await change.ExecuteNonQueryAsync();
            await using var state = connection.CreateCommand();
            state.CommandText = "SELECT state FROM mutation_intents WHERE operation_id=$original;";
            state.Parameters.AddWithValue("$original", original.OperationId.ToString("D"));
            Assert.AreEqual((long)MirrorPulseMutationState.Superseded, await state.ExecuteScalarAsync());
            Assert.IsNull(await catalog.ReadMutationAsync(execution.Steps[0].OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
    }

    [TestMethod]
    public async Task PreparationNeverAcknowledgesOrChangesOriginalOfficialJournalRows()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.CfSharpStateDatabasePath)!);
        await using var official = await new SqliteCloudStateStoreFactory(fixture.Paths.CfSharpStateDatabasePath)
            .OpenAsync(new CloudStateStoreContext(fixture.Paths.SyncRootPath));
        var originals = new List<CloudOperationJournalEntry>();
        await using (var transaction = await official.BeginTransactionAsync())
        {
            await transaction.Items.UpsertAsync(new CloudItemState(fixture.Plan.ItemId, "synthetic-unaccepted-item",
                "Local/original.txt", CloudItemKind.File, fixture.Revision, null, false, DateTimeOffset.UtcNow));
            foreach (var command in fixture.Window)
            {
                var entry = new CloudOperationJournalEntry(command.OperationId,
                    command.Kind == MirrorPulseWorkerChangeKind.Move ? CloudStateOperationKind.Move : CloudStateOperationKind.ContentUpdate,
                    command.ItemId, new byte[] { 11, 22, 33 }, command.ObservedAt);
                await transaction.Operations.EnqueueAsync(entry);
                originals.Add((await transaction.Operations.GetAsync(command.OperationId))!);
            }
            await transaction.CommitAsync();
        }
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareJournalCoalescingAsync(fixture.Plan);
        await PrepareAsync(catalog, fixture);
        await using var reading = await official.BeginTransactionAsync();
        foreach (var original in originals)
        {
            var pending = (await reading.Operations.GetAsync(original.OperationId))!;
            Assert.AreEqual(original.OperationId, pending.OperationId);
            Assert.AreEqual(original.Sequence, pending.Sequence);
            Assert.AreEqual(original.ItemId, pending.ItemId);
            Assert.AreEqual(original.Kind, pending.Kind);
            Assert.AreEqual(original.AttemptCount, pending.AttemptCount);
            CollectionAssert.AreEqual(original.Payload.ToArray(), pending.Payload.ToArray());
        }
        await reading.RollbackAsync();
        Assert.IsEmpty(await catalog.ReadIncompleteMutationsAsync());
    }

    [TestMethod]
    public async Task Schema24UpgradePreservesOriginalPlanAndSupersededMutationWithoutInventingExecution()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var intent = fixture.Intent(fixture.Window[0]);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareMutationAsync(intent);
            await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(fixture.Plan, fixture.Window, [intent]);
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE journal_coalescing_steps; DROP TABLE journal_coalescing_executions; PRAGMA user_version=24;";
            await downgrade.ExecuteNonQueryAsync();
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
        Assert.AreEqual(intent, (await reopened.ReadMutationAsync(intent.OperationId))!.Intent);
        Assert.AreEqual(MirrorPulseMutationState.Superseded, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
        Assert.AreEqual(JsonSerializer.Serialize(fixture.Plan), JsonSerializer.Serialize((await reopened.ReadJournalCoalescingPlansAsync()).Single()));
        Assert.HasCount(1, (await PrepareAsync(reopened, fixture)).OriginalMutations);
    }

    private static Task<MirrorPulseJournalCoalescingExecution> PrepareAsync(MirrorPulseProductCatalog catalog, Fixture fixture) =>
        catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);

    internal sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-coalescing-execution", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public MirrorPulseWorkerChangeCommand[] Window { get; }
        public MirrorPulseJournalCoalescingPlan Plan { get; }
        public MirrorPulseCoalescingContent? Content { get; }
        public string? Revision { get; }
        private readonly MirrorPulseUploadBinding _binding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile);

        public Fixture(MirrorPulseCoalescedEffect effect, string? baseline = null)
        {
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            InstanceId instance = InstanceId.New();
            Guid item = Guid.NewGuid();
            DateTimeOffset observed = DateTimeOffset.UtcNow;
            MirrorPulseWorkerChangeCommand Command(long sequence, MirrorPulseWorkerChangeKind kind, string path, string? previous = null) =>
                new(Guid.NewGuid(), sequence, instance, "files", kind, path, previous is null ? null : "files", previous, false, item, observed.AddSeconds(sequence));
            Window = effect switch
            {
                MirrorPulseCoalescedEffect.CreateFile => [Command(1, MirrorPulseWorkerChangeKind.Create, "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Move, "final.txt", "original.txt")],
                MirrorPulseCoalescedEffect.UpdateFile => [Command(1, MirrorPulseWorkerChangeKind.ContentUpdate, "original.txt"), Command(2, MirrorPulseWorkerChangeKind.ContentUpdate, "original.txt")],
                MirrorPulseCoalescedEffect.MoveFile => [Command(1, MirrorPulseWorkerChangeKind.Move, "middle.txt", "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Move, "final.txt", "middle.txt")],
                MirrorPulseCoalescedEffect.MoveAndUpdateFile => [Command(1, MirrorPulseWorkerChangeKind.ContentUpdate, "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Move, "final.txt", "original.txt")],
                MirrorPulseCoalescedEffect.DeleteFile => [Command(1, MirrorPulseWorkerChangeKind.Move, "final.txt", "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Delete, "final.txt")],
                MirrorPulseCoalescedEffect.VerifyRemoteAbsence => [Command(1, MirrorPulseWorkerChangeKind.Create, "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Move, "final.txt", "original.txt"), Command(3, MirrorPulseWorkerChangeKind.Delete, "final.txt")],
                _ => [Command(1, MirrorPulseWorkerChangeKind.Move, "middle.txt", "original.txt"), Command(2, MirrorPulseWorkerChangeKind.Move, "original.txt", "middle.txt")],
            };
            Plan = MirrorPulseJournalCoalescingPlanner.TryPlan(Window, item, 100, new HashSet<Guid>())!;
            Assert.AreEqual(effect, Plan.Effect);
            Revision = effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null : baseline ?? "baseline";
            Content = effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.UpdateFile or MirrorPulseCoalescedEffect.MoveAndUpdateFile
                ? new(4, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), _binding) : null;
        }

        public MirrorPulseMutationIntent Intent(MirrorPulseWorkerChangeCommand command) => new(command.OperationId, command.InstanceId,
            command.RootKey, command.Kind, command.RelativePath, command.PreviousRelativePath, false, Revision,
            command.Kind is MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate ? 4 : null,
            command.Kind is MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate ? new string('A', 64) : null,
            MirrorPulseMutationOrigin.Journal, command.Kind is MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate ? _binding : null);

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
