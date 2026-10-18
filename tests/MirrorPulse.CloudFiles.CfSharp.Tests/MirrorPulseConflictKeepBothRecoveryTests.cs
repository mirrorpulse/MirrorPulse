using System.Security.Cryptography;
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
public sealed class MirrorPulseConflictKeepBothRecoveryTests
{
    [TestMethod]
    public async Task CorruptPreservationDoesNotBlockAPeerAndItsOriginalFaultHealsAfterRepair()
    {
        await using var fixture = await Fixture.OpenAsync();
        Guid bad = await fixture.PrepareAsync(), peer = await fixture.PrepareAsync();
        await fixture.ChangeActionAsync(bad, "keeplocal");
        var recovery = fixture.Recovery(fixture.ResumeLocalBoundaryAsync);
        await recovery.RepairAsync(default);
        Assert.IsTrue((await fixture.Catalog.ReadConflictKeepBothAsync(peer))!.OriginalRetriesPrepared);
        Assert.AreEqual(new MirrorPulseJournalPumpHealth(false, 1, "ConflictKeepBothRecoveryFailed"), fixture.Runner.Health);
        string log = await File.ReadAllTextAsync(fixture.Log.FilePath);
        Assert.IsFalse(log.Contains("private-note", StringComparison.Ordinal)); StringAssert.Contains(log, bad.ToString("D"));
        await fixture.ChangeActionAsync(bad, "keepboth"); await recovery.RepairAsync(default);
        Assert.IsTrue(fixture.Runner.Health.Healthy);
        Assert.IsTrue((await fixture.Catalog.ReadConflictKeepBothAsync(bad))!.OriginalRetriesPrepared);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(bad))!.State, "A prepared retry is not remote acceptance or original acknowledgement.");
    }

    [TestMethod]
    public async Task RecoveryRotatesPastSixtyFourFailuresWithoutResolvingOrAcknowledgingAnyOriginal()
    {
        await using var fixture = await Fixture.OpenAsync();
        for (int index = 0; index < 65; index++) await fixture.PrepareAsync();
        var attempted = new HashSet<Guid>();
        var recovery = fixture.Recovery((id, _) => { Assert.IsTrue(attempted.Add(id)); throw new IOException("private-note source failure"); });
        await recovery.RepairAsync(default); Assert.HasCount(64, attempted); Assert.AreEqual(64, fixture.Runner.Health.PendingFaults);
        await recovery.RepairAsync(default); Assert.HasCount(65, attempted); Assert.AreEqual(65, fixture.Runner.Health.PendingFaults);
        Assert.HasCount(130, await fixture.Catalog.ReadUploadConflictsAsync());
        var originals = new List<Guid>();
        foreach (Guid id in attempted) originals.AddRange((await fixture.Catalog.ReadConflictKeepBothAsync(id))!.Intent.OriginalOperationIds);
        await using var transaction = await fixture.Store.BeginTransactionAsync();
        foreach (Guid id in originals) Assert.IsNotNull(await transaction.Operations.GetAsync(id));
        await transaction.RollbackAsync();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task DisabledOrClosedRootsNeverEnterTheSourceOrRetryMetadataBoundary(bool enabled, bool closed)
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        await using var work = closed ? new MirrorPulseNamespaceWorkAdmission([fixture.Root.RootId]) : null;
        var recovery = fixture.Recovery((_, _) => throw new AssertFailedException("Unavailable roots cannot access source or original metadata."), enabled, work);
        await recovery.RepairAsync(default);
        Assert.IsNull((await fixture.Catalog.ReadConflictKeepBothAsync(command))!.Copy); Assert.IsTrue(fixture.Runner.Health.Healthy);
        Assert.IsFalse(File.Exists(fixture.Log.FilePath));
    }

    [TestMethod]
    public async Task AReturnedCallbackCannotFakeTheDurableOriginalRetryBoundary()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var recovery = fixture.Recovery(async (id, token) =>
        {
            var decision = (await fixture.Catalog.ReadConflictKeepBothAsync(id, token))!;
            return new(id, decision.Intent.ConflictId, MirrorPulseConflictAction.KeepBoth, null, DateTimeOffset.UtcNow);
        });
        await recovery.RepairAsync(default);
        Assert.AreEqual(1, fixture.Runner.Health.PendingFaults);
        Assert.IsFalse((await fixture.Catalog.ReadConflictKeepBothAsync(command))!.OriginalRetriesPrepared);
    }

    [TestMethod]
    public async Task RetryMetadataIsResetAtomicallyAndLeavesPayloadSequenceAndPendingOriginalsIntact()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var decision = (await fixture.Catalog.ReadConflictKeepBothAsync(command))!;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(fixture.Store, decision, default).AsTask());
        await fixture.ResumeLocalBoundaryAsync(command, default);
        decision = (await fixture.Catalog.ReadConflictKeepBothAsync(command))!;
        await MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(fixture.Store, decision, default);
        await using var transaction = await fixture.Store.BeginTransactionAsync();
        foreach (Guid id in decision.Intent.OriginalOperationIds)
        {
            var original = (await transaction.Operations.GetAsync(id))!;
            Assert.IsNotNull(original); Assert.AreEqual(0, original.AttemptCount); Assert.IsNull(original.RetryAfter);
            CollectionAssert.AreEqual(Fixture.OriginalPayload, original.Payload.ToArray()); Assert.IsGreaterThan(0L, original.Sequence);
        }
        await transaction.RollbackAsync();
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    public async Task AChangedLaterOriginalRollsBackAllEarlierRetryMetadataUpdates()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var decision = await fixture.AuthorizeOnlyAsync(command);
        Guid first = decision.Intent.OriginalOperationIds[0], last = decision.Intent.OriginalOperationIds[^1];
        await using (var transaction = await fixture.Store.BeginTransactionAsync())
        {
            var entry = (await transaction.Operations.GetAsync(last))!;
            Guid replacement = Guid.NewGuid();
            await transaction.Items.UpsertAsync(new(replacement, "replacement-" + replacement.ToString("N"),
                "Docs/replacement-" + replacement.ToString("N") + ".bin", CloudItemKind.File, "old", null, false, DateTimeOffset.UtcNow));
            await transaction.Operations.UpdateAsync(new(entry.OperationId, entry.Kind, replacement, entry.Payload.Span,
                entry.CreatedAt, entry.AttemptCount, entry.RetryAfter, entry.Sequence));
            await transaction.CommitAsync();
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(fixture.Store, decision, default).AsTask());
        await using var reading = await fixture.Store.BeginTransactionAsync();
        var retained = (await reading.Operations.GetAsync(first))!;
        Assert.AreEqual(4, retained.AttemptCount); Assert.IsNotNull(retained.RetryAfter);
        CollectionAssert.AreEqual(Fixture.OriginalPayload, retained.Payload.ToArray()); await reading.RollbackAsync();
        Assert.IsFalse((await fixture.Catalog.ReadConflictKeepBothAsync(command))!.OriginalRetriesPrepared);
    }

    [TestMethod]
    public async Task ResetDoesNotRecreateAMissingOriginalOrModifyAnIndependentSuccessor()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var decision = await fixture.AuthorizeOnlyAsync(command);
        Guid missing = decision.Intent.OriginalOperationIds[0], successor = Guid.NewGuid();
        var later = new CloudOperationJournalEntry(successor, CloudStateOperationKind.MetadataUpdate, decision.Intent.ItemId,
            Fixture.OriginalPayload, DateTimeOffset.UtcNow, 9, DateTimeOffset.UtcNow.AddHours(2));
        await using (var transaction = await fixture.Store.BeginTransactionAsync())
        {
            await transaction.Operations.RemoveAsync(missing); await transaction.Operations.EnqueueAsync(later); await transaction.CommitAsync();
        }
        await MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(fixture.Store, decision, default);
        await using var reading = await fixture.Store.BeginTransactionAsync();
        Assert.IsNull(await reading.Operations.GetAsync(missing));
        var untouched = (await reading.Operations.GetAsync(successor))!;
        Assert.AreEqual(9, untouched.AttemptCount); Assert.AreEqual(later.RetryAfter, untouched.RetryAfter);
        CollectionAssert.AreEqual(later.Payload.ToArray(), untouched.Payload.ToArray()); await reading.RollbackAsync();
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    public async Task CancellationBeforeRetryMetadataAdmissionLeavesEveryOriginalPendingAndDeferred()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        var decision = await fixture.AuthorizeOnlyAsync(command);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(fixture.Store, decision, stop.Token).AsTask());
        await using var reading = await fixture.Store.BeginTransactionAsync();
        foreach (Guid id in decision.Intent.OriginalOperationIds)
        {
            var entry = (await reading.Operations.GetAsync(id))!;
            Assert.AreEqual(4, entry.AttemptCount); Assert.IsNotNull(entry.RetryAfter);
        }
        await reading.RollbackAsync(); Assert.IsFalse((await fixture.Catalog.ReadConflictKeepBothAsync(command))!.OriginalRetriesPrepared);
    }

    [TestMethod]
    public async Task AChangedCommittedCopyCannotAuthorizeTheOriginalsOrReadANewSource()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        string path = await fixture.PreserveOnlyAsync(command);
        await File.WriteAllTextAsync(path, "changed private copy");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.ResumeLocalBoundaryAsync(command, default).AsTask());
        var decision = (await fixture.Catalog.ReadConflictKeepBothAsync(command))!;
        Assert.IsFalse(decision.RetryAuthorized); Assert.IsNull(decision.Copy);
        Assert.HasCount(2, await fixture.Catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task TheVerifiedRemoteCopyHandleExcludesWritersUntilAuthorizationFinishes()
    {
        await using var fixture = await Fixture.OpenAsync(); Guid command = await fixture.PrepareAsync();
        string path = await fixture.PreserveOnlyAsync(command);
        var decision = (await fixture.Catalog.ReadConflictKeepBothAsync(command))!;
        var selected = (await fixture.Catalog.ReadUploadConflictAsync(decision.Intent.ConflictId))!;
        await using var retained = await new MirrorPulseConflictCopyStore(fixture.Paths).OpenRemoteDecisionAsync(command, selected, decision.Intent.RemoteSource);
        await Assert.ThrowsExactlyAsync<IOException>(() => File.WriteAllTextAsync(path, "interleaved writer"));
        CollectionAssert.AreEqual(Fixture.OriginalPayload, await File.ReadAllBytesAsync(path));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] OriginalPayload = [1, 2, 3];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-keep-both-recovery", Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths => new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        internal RootRegistration Root { get; } = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.keep-both-recovery"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal ICloudStateStore Store { get; private set; } = null!;
        internal MirrorPulseJournalPumpRunner Runner { get; } = new();
        internal LocalRollingLogWriter Log { get; private set; } = null!;
        internal static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.Paths.SyncRootPath);
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            fixture.Store = await new MirrorPulseCfSharpStateSession(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
            fixture.Log = new(Path.Combine(fixture.Paths.DataRootPath, "logs")); return fixture;
        }
        internal MirrorPulseConflictKeepBothRecovery Recovery(Func<Guid, CancellationToken, ValueTask<MirrorPulseConflictResolution>> resume,
            bool enabled = true, MirrorPulseNamespaceWorkAdmission? work = null) => new(Catalog, resume, new(Paths.SyncRootPath, [Root]), _ => enabled, Runner, Log, work);

        internal async Task<Guid> PrepareAsync()
        {
            Guid item = Guid.NewGuid(); string path = "private-note-" + item.ToString("N") + ".bin";
            MirrorPulseWorkerChangeCommand[] window = [new(Guid.NewGuid(), 1, Root.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                path, null, null, false, item, DateTimeOffset.UtcNow), new(Guid.NewGuid(), 2, Root.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                path, null, null, false, item, DateTimeOffset.UtcNow)];
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
            await Catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, "old", new(3, Convert.ToHexString(SHA256.HashData(OriginalPayload)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.None)));
            var effect = await Catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
            await Catalog.TransitionMutationAsync(effect.Intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await Catalog.TransitionMutationAsync(effect.Intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
            await using (var transaction = await Store.BeginTransactionAsync())
            {
                await transaction.Items.UpsertAsync(new(item, "remote-" + item.ToString("N"), "Docs/" + path,
                    CloudItemKind.File, "old", null, false, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }
            Guid conflict = Guid.Empty;
            foreach (var member in plan.Members)
            {
                conflict = Guid.NewGuid(); await Catalog.SaveUploadConflictAsync(new(conflict, Root.InstanceId, member.OperationId.ToString("D"), "Docs/" + path,
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "old", "selected", DateTimeOffset.UtcNow));
                await using var transaction = await Store.BeginTransactionAsync();
                await transaction.Operations.EnqueueAsync(new(member.OperationId, CloudStateOperationKind.MetadataUpdate, item, OriginalPayload, DateTimeOffset.UtcNow,
                    4, DateTimeOffset.UtcNow.AddHours(1))); await transaction.CommitAsync();
            }
            Guid command = Guid.NewGuid(); await Catalog.SaveUserCommandAsync(new(command, "keepboth", conflict.ToString("D"), "pending"));
            await Catalog.PrepareConflictKeepBothAsync(command, conflict, item, "Docs/" + path, new(1, Root.InstanceId.ToString(), "docs", path, "remote-object", "selected", 3), null);
            return command;
        }

        internal async ValueTask<MirrorPulseConflictResolution> ResumeLocalBoundaryAsync(Guid id, CancellationToken token)
        {
            var decision = (await Catalog.ReadConflictKeepBothAsync(id, token))!;
            var selected = (await Catalog.ReadUploadConflictAsync(decision.Intent.ConflictId, token))!;
            var copies = new MirrorPulseConflictCopyStore(Paths);
            await copies.PreserveRemoteDecisionAsync(id, selected, decision.Intent.RemoteSource, _ => ValueTask.FromResult<Stream>(new MemoryStream(OriginalPayload, writable: false)), token);
            await using var copy = await copies.OpenRemoteDecisionAsync(id, selected, decision.Intent.RemoteSource, token);
            await Catalog.RecordConflictKeepBothCopyAsync(id, copy.Proof, token);
            await Catalog.AuthorizeJournalCoalescingRetryAsync(id, selected.ConflictId, true, token);
            decision = (await Catalog.ReadConflictKeepBothAsync(id, token))!;
            await MirrorPulseUploadConflictActions.ResetKeepBothOriginalRetriesAsync(Store, decision, token);
            await Catalog.RecordConflictKeepBothOriginalRetriesPreparedAsync(id, token);
            return new(id, selected.ConflictId, MirrorPulseConflictAction.KeepBoth, copy.Path, DateTimeOffset.UtcNow);
        }
        internal async Task<MirrorPulseConflictKeepBothDecision> AuthorizeOnlyAsync(Guid id)
        {
            await PreserveOnlyAsync(id);
            var decision = (await Catalog.ReadConflictKeepBothAsync(id))!;
            var selected = (await Catalog.ReadUploadConflictAsync(decision.Intent.ConflictId))!;
            await using var copy = await new MirrorPulseConflictCopyStore(Paths).OpenRemoteDecisionAsync(id, selected, decision.Intent.RemoteSource);
            await Catalog.RecordConflictKeepBothCopyAsync(id, copy.Proof);
            await Catalog.AuthorizeJournalCoalescingRetryAsync(id, selected.ConflictId, true);
            return (await Catalog.ReadConflictKeepBothAsync(id))!;
        }
        internal async Task<string> PreserveOnlyAsync(Guid id)
        {
            var decision = (await Catalog.ReadConflictKeepBothAsync(id))!;
            var selected = (await Catalog.ReadUploadConflictAsync(decision.Intent.ConflictId))!;
            return await new MirrorPulseConflictCopyStore(Paths).PreserveRemoteDecisionAsync(id, selected, decision.Intent.RemoteSource,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(OriginalPayload, writable: false)));
        }
        internal async Task ChangeActionAsync(Guid id, string action)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE user_commands SET action=$action WHERE command_id=$id;";
            command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$id", id.ToString("D")); await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync() { Log.Dispose(); await Store.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
