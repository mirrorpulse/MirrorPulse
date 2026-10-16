using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseConflictDeletionRecoveryTests
{
    [TestMethod]
    public async Task ACorruptDecisionDoesNotBlockAPeerAndDirectCompletionHealsItsRetainedFault()
    {
        await using var fixture = await Fixture.OpenAsync();
        Guid failed = await fixture.PrepareAsync(), peer = await fixture.PrepareAsync();
        await fixture.ChangeAuthorizationAsync(failed, "keeplocal");
        var recovery = fixture.Recovery(async (id, token) =>
        {
            Assert.AreEqual(peer, id); return await fixture.CompleteAsync(id, token);
        });
        await recovery.RepairAsync(default);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(failed))!.State);
        Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(peer))!.State);
        Assert.AreEqual(new MirrorPulseJournalPumpHealth(false, 1, "ConflictDeletionRecoveryFailed"), fixture.Runner.Health);
        string log = await File.ReadAllTextAsync(fixture.Log.FilePath);
        StringAssert.Contains(log, "ConflictDeletionRecoveryFailed");
        Assert.IsFalse(log.Contains("private-note", StringComparison.Ordinal));
        await fixture.ChangeAuthorizationAsync(failed, "deleteremote");
        await fixture.CompleteAsync(failed, default); // Models completion through the separately tested Host boundary.
        await recovery.RepairAsync(default);
        Assert.IsTrue(fixture.Runner.Health.Healthy);
    }

    [TestMethod]
    public async Task TheRecoveryBudgetRotatesPastSixtyFourFailuresWithoutChangingOriginalOwnership()
    {
        await using var fixture = await Fixture.OpenAsync();
        var commands = new List<Guid>();
        for (int index = 0; index < 65; index++) commands.Add(await fixture.PrepareAsync());
        var attempted = new HashSet<Guid>();
        var recovery = fixture.Recovery((id, _) =>
        { Assert.IsTrue(attempted.Add(id)); throw new IOException("Synthetic unavailable Worker; private-note must stay out of logs."); });
        await recovery.RepairAsync(default);
        Assert.HasCount(64, attempted); Assert.AreEqual(64, fixture.Runner.Health.PendingFaults);
        await recovery.RepairAsync(default);
        Assert.HasCount(65, attempted); Assert.AreEqual(65, fixture.Runner.Health.PendingFaults);
        foreach (Guid command in commands)
        {
            var retained = (await fixture.Catalog.ReadConflictDeletionAsync(command))!;
            Assert.AreEqual(MirrorPulseConflictDeletionPhase.Prepared, retained.Phase);
            Assert.AreEqual(command, await fixture.Catalog.ReadConflictDeletionOwnerAsync(retained.Intent.OriginalOperationIds[0]));
            Assert.AreEqual(MirrorPulseMutationState.Prepared, (await fixture.Catalog.ReadMutationAsync(retained.Intent.RemoteDeletion.OperationId))!.State);
        }
    }

    [TestMethod]
    public async Task ADisabledInstanceRetainsItsDecisionWithoutCallingTheHostOperationBoundary()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var recovery = fixture.Recovery((_, _) => throw new AssertFailedException("Offline decisions must not access local files or Workers."), enabled: false);
        await recovery.RepairAsync(default);
        Assert.AreEqual(MirrorPulseConflictDeletionPhase.Prepared, (await fixture.Catalog.ReadConflictDeletionAsync(command))!.Phase);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
        Assert.IsTrue(fixture.Runner.Health.Healthy); Assert.IsFalse(File.Exists(fixture.Log.FilePath));
    }

    [TestMethod]
    public async Task CancellationDoesNotBecomeADecisionFaultOrConsumeAuthorization()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var recovery = fixture.Recovery((_, _) => throw new AssertFailedException("Canceled polling cannot enter a Host operation."));
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => recovery.RepairAsync(cancellation.Token).AsTask());
        Assert.IsTrue(fixture.Runner.Health.Healthy); Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
    }

    // These fixtures exercise real product SQLite and recovery admission. They
    // model completion receipts, without claiming native deletion or official ACK.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-deletion-recovery", Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths { get; }
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        private readonly RootRegistration _root;
        private readonly MirrorPulseRootRouter _router;
        internal MirrorPulseJournalPumpRunner Runner { get; } = new();
        internal LocalRollingLogWriter Log { get; }
        private Fixture()
        {
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            _root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.deletion-recovery"), InstanceId.New(),
                new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
            _router = new(Paths.SyncRootPath, [_root]); Log = new(Path.Combine(Paths.DataRootPath, "logs"));
        }
        internal static async Task<Fixture> OpenAsync()
        { var fixture = new Fixture(); fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); return fixture; }
        internal MirrorPulseConflictDeletionRecovery Recovery(Func<Guid, CancellationToken, ValueTask<MirrorPulseConflictResolution>> resume, bool enabled = true) =>
            new(Catalog, resume, _router, _ => enabled, Runner, Log);
        internal async Task<Guid> PrepareAsync()
        {
            Guid original = Guid.NewGuid(), conflict = Guid.NewGuid(), command = Guid.NewGuid();
            string path = "private-note-" + original.ToString("N") + ".txt";
            var intent = new MirrorPulseMutationIntent(original, _root.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                path, null, false, "old", 1, new string('A', 64), MirrorPulseMutationOrigin.Journal);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => new MirrorPulseMutationExecutor(Catalog).ExecuteAsync(intent,
                _ => throw new MirrorPulseWorkerMutationConflictException("old", "observed"), (_, _) => throw new AssertFailedException(), default).AsTask());
            await Catalog.SaveUploadConflictAsync(new(conflict, _root.InstanceId, original.ToString("D"), "Docs/" + path,
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "old", "observed", DateTimeOffset.UtcNow));
            await Catalog.SaveUserCommandAsync(new(command, "deleteremote", conflict.ToString("D"), "pending"));
            await Catalog.PrepareConflictDeletionAsync(command, conflict, "docs", Guid.NewGuid(), "Docs/" + path, null);
            return command;
        }
        internal async ValueTask<MirrorPulseConflictResolution> CompleteAsync(Guid command, CancellationToken token)
        {
            var decision = (await Catalog.ReadConflictDeletionAsync(command, token))!;
            Guid remote = decision.Intent.RemoteDeletion.OperationId;
            await Catalog.TransitionMutationAsync(remote, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing, cancellationToken: token);
            await Catalog.TransitionMutationAsync(remote, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, cancellationToken: token);
            await Catalog.TransitionMutationAsync(remote, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged, cancellationToken: token);
            while (decision.Phase != MirrorPulseConflictDeletionPhase.Completed)
            {
                var next = (MirrorPulseConflictDeletionPhase)((int)decision.Phase + 1);
                await Catalog.AdvanceConflictDeletionAsync(command, decision.Phase, next, token); decision = decision with { Phase = next };
            }
            return new(command, decision.Intent.ConflictId, MirrorPulseConflictAction.DeleteRemote, null, DateTimeOffset.UtcNow);
        }
        internal async Task ChangeAuthorizationAsync(Guid command, string action)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE user_commands SET action=$action WHERE command_id=$id;";
            update.Parameters.AddWithValue("$action", action); update.Parameters.AddWithValue("$id", command.ToString("D")); await update.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync() { Log.Dispose(); await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
