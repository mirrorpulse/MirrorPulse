using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CfSharp;
using CfSharp.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using MirrorPulse.Worker.ProtocolFixture;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalCoalescingExecutorTests
{
    private static readonly byte[] FinalBytes = [1, 2, 3, 4];
    private static readonly string[] MoveAndUpload = ["Move", "Upload"];

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile, "Upload")]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "Upload")]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile, "Move")]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile, "Move,Upload")]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile, "Delete")]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence, "")]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged, "")]
    public async Task OwnedEffectsKeepOriginalIntentsAndUseOnlyTheFinalRemoteProgram(MirrorPulseCoalescedEffect effect, string writes)
    {
        using var fixture = new Fixture(effect);
        var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = fixture.Intent(fixture.Window[0]);
        await PrepareAsync(catalog, fixture);
        MirrorPulseMutationRecord retained = (await catalog.ReadMutationAsync(original.OperationId))!;
        var executor = Executor(catalog, remote);
        var completed = await executor.ExecuteAsync(fixture.Plan.PlanId, OpenContent);
        CollectionAssert.AreEqual(writes.Length == 0 ? Array.Empty<string>() : writes.Split(','), remote.Writes.Select(write => write.Kind).ToArray());
        Assert.AreEqual(retained, await catalog.ReadMutationAsync(original.OperationId));
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged &&
            record.Intent.Origin == MirrorPulseMutationOrigin.CoalescedJournal && record.ExecutionEvidence == MirrorPulseMutationExecutionEvidence.Started));
        Assert.IsTrue(completed.All(record => fixture.Window.All(command => command.OperationId != record.Intent.OperationId)));
        if (effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile)
        {
            Assert.AreEqual("moved/baseline", completed[0].AcceptedRevision);
            Assert.AreEqual("moved/baseline", completed[1].Intent.ExpectedRevision);
            Assert.AreNotEqual(completed[0].Intent.OperationId, completed[1].Intent.OperationId);
        }
        if (fixture.Content is not null) CollectionAssert.AreEqual(FinalBytes, remote.Read(fixture.Plan.FinalPath));
        if (effect == MirrorPulseCoalescedEffect.DeleteFile) Assert.IsFalse(remote.Exists(fixture.Plan.OriginalPath));
        int writesBeforeReplay = remote.Writes.Count;
        await executor.ExecuteAsync(fixture.Plan.PlanId, (_, _) => throw new AssertFailedException("Completed effects cannot read or resend local content."));
        Assert.HasCount(writesBeforeReplay, remote.Writes);
        Assert.IsEmpty(await new MirrorPulseMutationProjectionRecovery(catalog).RepairAsync(
            (_, _) => throw new AssertFailedException("Derived effect IDs are not official journal IDs."),
            (_, _) => throw new AssertFailedException("Original projection has not been confirmed."), default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(completed[0].Intent));
        Assert.AreEqual(original, retained.Intent);
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile, "Upload")]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile, "Upload")]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile, "Delete")]
    public async Task LostRepliesRecoverAcrossCatalogRestartByReadbackWithoutAnotherWrite(MirrorPulseCoalescedEffect effect, string lostReply)
    {
        using var fixture = new Fixture(effect);
        var remote = new FileRemote(fixture) { LoseReplyAfter = lostReply };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareAsync(catalog, fixture);
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            var preparation = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
            Assert.AreEqual(MirrorPulseMutationState.Ambiguous, (await catalog.ReadMutationAsync(preparation.Steps[^1].OperationId))!.State);
            Assert.IsEmpty(await new MirrorPulseMutationProjectionRecovery(catalog).RepairAsync(
                (_, _) => throw new AssertFailedException("No original journal query is permitted for a derived effect."),
                (_, _) => throw new AssertFailedException(), default));
        }
        int writes = remote.Writes.Count;
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var completed = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("Readback recovery must not reopen local bytes."));
        Assert.IsTrue(completed.All(record => record.State == MirrorPulseMutationState.Acknowledged));
        Assert.HasCount(writes, remote.Writes);
        Assert.AreEqual(MirrorPulseMutationState.Superseded, (await reopened.ReadMutationAsync(fixture.Window[0].OperationId))!.State);
    }

    [TestMethod]
    public async Task LostMoveReplyNeverAuthorizesTheFollowingUploadOrAnotherMove()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var remote = new FileRemote(fixture) { LoseReplyAfter = "Move" };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareAsync(catalog, fixture);
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId,
                (_, _) => throw new AssertFailedException("Unknown move must stop before upload.")).AsTask());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            (_, _) => throw new AssertFailedException("The accepted move revision is not known.")).AsTask());
        var preparation = (await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
        Assert.HasCount(1, remote.Writes);
        Assert.IsNull(await reopened.ReadMutationAsync(preparation.Steps[1].OperationId));
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => reopened.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 1));
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task ChangedBaselineOrExistingDestinationRetainsConflictWithoutRemoteWrites(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect);
        var remote = new FileRemote(fixture);
        remote.Seed(effect == MirrorPulseCoalescedEffect.CreateFile ? fixture.Plan.FinalPath : fixture.Plan.OriginalPath,
            "unexpected-remote-revision", [8, 8, 8]);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareAsync(catalog, fixture);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
        var preparation = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(preparation.Steps[0].OperationId))!.State);
    }

    [TestMethod]
    public async Task UploadCannotBePreparedBeforeTheMoveCompletesAndUnsupportedDispatchIsNotNeverStarted()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var remote = new FileRemote(fixture) { RejectAsUnsupported = true };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareAsync(catalog, fixture);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        var preparation = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
        var retained = (await catalog.ReadMutationAsync(preparation.Steps[0].OperationId))!;
        Assert.AreEqual(MirrorPulseMutationState.Prepared, retained.State);
        Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, retained.ExecutionEvidence);
        remote.RejectAsUnsupported = false;
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow("origin")]
    [DataRow("path")]
    [DataRow("revision")]
    [DataRow("owner")]
    public async Task ChangedDerivedIntentOrOwnershipCannotEnterExecutionOrGenericJournalRepair(string field)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareAsync(catalog, fixture);
        var record = await catalog.PrepareJournalCoalescingMutationAsync(fixture.Plan.PlanId, 0);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT payload FROM mutation_intents WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", record.Intent.OperationId.ToString("D"));
            var json = JsonNode.Parse((byte[])(await query.ExecuteScalarAsync())!)!;
            json[field switch { "origin" => "Origin", "path" => "RelativePath", _ => "ExpectedRevision" }] = field == "origin" ? JsonValue.Create(0) : JsonValue.Create("changed");
            await using var change = connection.CreateCommand();
            change.CommandText = field == "owner" ? "DELETE FROM journal_coalescing_steps WHERE operation_id=$id;"
                : "UPDATE mutation_intents SET payload=$payload WHERE operation_id=$id;";
            change.Parameters.AddWithValue("$id", record.Intent.OperationId.ToString("D"));
            change.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(json));
            await change.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
        Assert.IsEmpty(remote.Writes);
        if (field is "origin" or "owner") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadIncompleteMutationsAsync());
    }

    [TestMethod]
    public async Task MultipleExecutorObjectsShareInstanceAdmissionAndDoNotReconcileAnActiveMove()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var remote = new FileRemote(fixture) { HoldMove = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareAsync(catalog, fixture);
        Task first = Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask();
        try
        {
            await remote.MoveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task second = Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask();
            Assert.IsFalse(second.IsCompleted);
            remote.HoldMove.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(MoveAndUpload, remote.Writes.Select(write => write.Kind).ToArray());
        }
        finally { remote.HoldMove.TrySetResult(); await first; }
    }

    [TestMethod]
    public async Task DurableAcceptanceCannotEnterOriginalProjectionRepairAndRecoversWithoutReupload()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        var remote = new FileRemote(fixture) { ReturnEmptyUploadRevision = true };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareAsync(catalog, fixture);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask());
            var preparation = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(preparation.Steps[0].OperationId))!.State);
            Assert.IsEmpty(await new MirrorPulseMutationProjectionRecovery(catalog).RepairAsync(
                (_, _) => throw new AssertFailedException("A derived ID cannot stand in for an original journal ID."),
                (_, _) => throw new AssertFailedException("Final native projection is not established."), default));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, (_, _) => throw new AssertFailedException("Recover by remote readback."));
        Assert.HasCount(1, remote.Writes);
    }

    [TestMethod]
    public async Task CatalogShutdownWaitsForAcceptedRemoteWorkAndRetainsItsUncertainIntent()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile);
        var remote = new FileRemote(fixture) { HoldMove = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreMoveCancellation = true };
        var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareAsync(catalog, fixture);
        var preparation = (await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId))!;
        Task executing = Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, OpenContent).AsTask();
        Task? stopping = null;
        try
        {
            await remote.MoveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stopping = catalog.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted, "The database owner must outlive accepted remote work.");
            await remote.MoveCancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            remote.HoldMove.SetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => executing);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            var record = (await reopened.ReadMutationAsync(preparation.Steps[0].OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.Executing, record.State);
            Assert.AreEqual(MirrorPulseMutationExecutionEvidence.Started, record.ExecutionEvidence);
            Assert.IsNull(await reopened.ReadMutationAsync(preparation.Steps[1].OperationId));
            Assert.HasCount(1, remote.Writes);
        }
        finally
        {
            remote.HoldMove.TrySetResult();
            try { await executing; } catch (OperationCanceledException) { }
            if (stopping is not null) await stopping;
            await catalog.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProductionSupervisorExecutesMoveAndUploadOverARealPipeWithoutAcknowledgingOriginalJournalRows()
    {
        byte[] seed = [8, 9, 10];
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.MoveAndUpdateFile, Convert.ToHexString(SHA256.HashData(seed)));
        var plan = fixture.Plan;
        string executable = Path.ChangeExtension(typeof(ProtocolFixtureMarker).Assembly.Location, ".exe");
        AdapterId adapter = AdapterId.Parse("example.protocolfixture");
        InstallId install = InstallId.New();
        var manifest = new AdapterManifest(1, adapter, "Fixture", "1.0.0", new(1, 2),
            new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
        var installed = new InstalledAdapter(manifest, install, Path.GetDirectoryName(executable)!, new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(adapter, install, plan.InstanceId, "Fixture", new Dictionary<string, string> { ["root.files.sourcePath"] = "files-source" },
            [], Path.Combine(fixture.Paths.DataRootPath, "files"), Path.Combine(fixture.Paths.DataRootPath, "transfers"), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        var root = new RootRegistration(adapter, plan.InstanceId, RootId.New(), "files", "Files", "Files", false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
        var topology = new MirrorPulseAdapterTopology([installed], [instance], [root]);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.SaveAdapterTopologyAsync(topology);
        Directory.CreateDirectory(fixture.Paths.SyncRootPath);
        await using var official = await new SqliteCloudStateStoreFactory(fixture.Paths.CfSharpStateDatabasePath).OpenAsync(new CloudStateStoreContext(fixture.Paths.SyncRootPath));
        var originals = new List<CloudOperationJournalEntry>();
        await using (var transaction = await official.BeginTransactionAsync())
        {
            await transaction.Items.UpsertAsync(new CloudItemState(plan.ItemId, "synthetic-local-binding", "Files/original.txt",
                CloudItemKind.File, fixture.Revision, null, false, DateTimeOffset.UtcNow));
            foreach (var command in fixture.Window)
            {
                await transaction.Operations.EnqueueAsync(new(command.OperationId, command.Kind == MirrorPulseWorkerChangeKind.Move
                    ? CloudStateOperationKind.Move : CloudStateOperationKind.ContentUpdate, plan.ItemId, new byte[] { 11, 22, 33 }, command.ObservedAt));
                originals.Add((await transaction.Operations.GetAsync(command.OperationId))!);
            }
            await transaction.CommitAsync();
        }
        await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
        await supervisor.StartAsync(topology);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await catalog.ReadInstanceRuntimeStateAsync(plan.InstanceId, timeout.Token))?.Phase != "Connected") await Task.Delay(25, timeout.Token);
        using var content = new MemoryStream(seed);
        Assert.AreEqual(fixture.Revision, await supervisor.UploadAsync(new(plan.InstanceId, "original.txt", null, content, seed.Length, Guid.NewGuid(), RootKey: "files"), timeout.Token));
        await PrepareAsync(catalog, fixture);
        var executor = new MirrorPulseJournalCoalescingExecutor(catalog, supervisor, supervisor, supervisor, new(supervisor, supervisor, supervisor));
        var effects = await executor.ExecuteAsync(plan.PlanId, (proof, token) =>
        {
            Assert.AreEqual(fixture.Content, proof);
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes));
        }, timeout.Token);
        Assert.HasCount(2, effects);
        string? observedBaseline = effects[1].Intent.ExpectedRevision;
        Assert.AreEqual(fixture.Revision, observedBaseline);
        Assert.IsNull(await supervisor.StatAsync(new(plan.InstanceId, "original.txt", "files"), timeout.Token));
        Assert.AreEqual(fixture.Content!.Sha256, await supervisor.StatAsync(new(plan.InstanceId, "final.txt", "files"), timeout.Token));
        await using Stream downloaded = await supervisor.ReadRangeAsync(new(plan.InstanceId, "final.txt", ReadOnlyMemory<byte>.Empty, 0, 4, "files"), timeout.Token);
        byte[] actual = new byte[4];
        await downloaded.ReadExactlyAsync(actual, timeout.Token);
        CollectionAssert.AreEqual(FinalBytes, actual);
        await executor.ExecuteAsync(plan.PlanId, (_, _) => throw new AssertFailedException("Accepted effects must not be resent."), timeout.Token);
        await using var reading = await official.BeginTransactionAsync(timeout.Token);
        foreach (var original in originals)
        {
            var pending = (await reading.Operations.GetAsync(original.OperationId, timeout.Token))!;
            Assert.AreEqual(original.OperationId, pending.OperationId);
            Assert.AreEqual(original.Sequence, pending.Sequence);
            Assert.AreEqual(original.ItemId, pending.ItemId);
            Assert.AreEqual(original.AttemptCount, pending.AttemptCount);
            CollectionAssert.AreEqual(original.Payload.ToArray(), pending.Payload.ToArray());
        }
        await reading.RollbackAsync(timeout.Token);
    }

    private static async Task PrepareAsync(MirrorPulseProductCatalog catalog, Fixture fixture)
    {
        var original = fixture.Intent(fixture.Window[0]);
        await catalog.PrepareMutationAsync(original);
        await catalog.PrepareJournalCoalescingWithUnstartedIntentsAsync(fixture.Plan, fixture.Window, [original]);
        await catalog.PrepareJournalCoalescingExecutionAsync(fixture.Plan, fixture.Window, fixture.Revision, fixture.Content);
    }

    private static ValueTask<Stream> OpenContent(MirrorPulseCoalescingContent expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.AreEqual(4L, expected.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(FinalBytes)), expected.Sha256);
        return ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes));
    }

    private static MirrorPulseJournalCoalescingExecutor Executor(MirrorPulseProductCatalog catalog, FileRemote remote) =>
        new(catalog, remote, remote, remote, new(remote, remote, remote));

    private sealed class FileRemote : IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerUploadTransport,
        IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        private readonly Fixture _fixture;
        private readonly string _directory;
        private readonly Dictionary<string, string> _revisions = new(StringComparer.Ordinal);
        public List<(string Kind, Guid? Id)> Writes { get; } = [];
        public string? LoseReplyAfter { get; set; }
        public bool RejectAsUnsupported { get; set; }
        public bool ReturnEmptyUploadRevision { get; set; }
        public bool IgnoreMoveCancellation { get; set; }
        public TaskCompletionSource? HoldMove { get; set; }
        public TaskCompletionSource MoveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MoveCancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FileRemote(Fixture fixture)
        {
            _fixture = fixture;
            _directory = Path.Combine(fixture.Paths.DataRootPath, "synthetic-remote");
            Directory.CreateDirectory(_directory);
            if (fixture.Revision is not null) Seed(fixture.Plan.OriginalPath, fixture.Revision, [9, 8, 7]);
        }

        public void Seed(string path, string revision, byte[] content) { File.WriteAllBytes(Path.Combine(_directory, path), content); _revisions[path] = revision; }
        public bool Exists(string path) => File.Exists(Path.Combine(_directory, path));
        public byte[] Read(string path) => File.ReadAllBytes(Path.Combine(_directory, path));
        private string? Revision(string path) => _revisions.GetValueOrDefault(path);
        private static void Check(string? expected, string? actual)
        {
            if (expected != actual) throw new MirrorPulseWorkerMutationConflictException(expected, actual);
        }
        private void Accepted(string kind, Guid? id)
        {
            Assert.IsNotNull(id);
            Assert.IsTrue(_fixture.Window.All(command => command.OperationId != id));
            Writes.Add((kind, id));
            if (LoseReplyAfter == kind) throw new IOException("Synthetic reply loss after the remote effect.");
        }

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(_fixture.Plan.InstanceId, request.InstanceId);
            Assert.AreEqual("files", request.RootKey);
            return ValueTask.FromResult(Revision(request.NormalizedPath));
        }

        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            if (RejectAsUnsupported) throw new NotSupportedException("Synthetic unsupported transport.");
            Check(request.ExpectedRevision, Revision(request.NormalizedPath));
            using var content = new MemoryStream();
            await request.Content.CopyToAsync(content, token);
            byte[] actual = content.ToArray();
            string revision = Convert.ToHexString(SHA256.HashData(actual));
            Assert.AreEqual(request.Length, actual.LongLength);
            Assert.AreEqual(request.ExpectedContentSha256, revision);
            Seed(request.NormalizedPath, revision, actual);
            Accepted("Upload", request.OperationId);
            return ReturnEmptyUploadRevision ? string.Empty : revision;
        }

        public async ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token)
        {
            if (RejectAsUnsupported) throw new NotSupportedException("Synthetic unsupported transport.");
            MoveEntered.TrySetResult();
            using var cancellation = token.Register(() => MoveCancellationRequested.TrySetResult());
            if (HoldMove is not null)
            {
                if (IgnoreMoveCancellation) await HoldMove.Task;
                else await HoldMove.Task.WaitAsync(token);
            }
            Check(request.ExpectedRevision, Revision(request.SourcePath));
            Check(null, Revision(request.DestinationPath));
            File.Move(Path.Combine(_directory, request.SourcePath), Path.Combine(_directory, request.DestinationPath));
            _revisions.Remove(request.SourcePath);
            string revision = "moved/" + request.ExpectedRevision;
            _revisions[request.DestinationPath] = revision;
            Accepted("Move", request.OperationId);
            return revision;
        }

        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (RejectAsUnsupported) throw new NotSupportedException("Synthetic unsupported transport.");
            Check(request.ExpectedRevision, Revision(request.NormalizedPath));
            File.Delete(Path.Combine(_directory, request.NormalizedPath));
            _revisions.Remove(request.NormalizedPath);
            Accepted("Delete", request.OperationId);
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] content = Read(request.NormalizedPath);
            return ValueTask.FromResult<Stream>(new MemoryStream(content, checked((int)request.Offset), checked((int)request.Length), false));
        }

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(_revisions.Select(pair => new MirrorPulseWorkerDirectoryEntry(
                pair.Key, pair.Value, "File", pair.Key, new FileInfo(Path.Combine(_directory, pair.Key)).Length, null, null, false)).ToArray(), ReadOnlyMemory<byte>.Empty, true));
        }
    }
}
