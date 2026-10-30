using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseConflictRemoteObservationRecoveryTests
{
    [TestMethod]
    public async Task MissingVersionRecoveryPublishesTheCompleteGroupAndRestartDoesNotReobserveOrAuthorizeIt()
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync();
        string original = JsonSerializer.Serialize(await fixture.Catalog.ReadMutationAsync(group.Effect));
        await fixture.Recovery().RepairAsync(default);
        var observed = await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect);
        Assert.IsNotNull(observed); Assert.IsNull(observed.OriginalErrorExpectedRevision); Assert.IsNull(observed.OriginalErrorActualRevision);
        Assert.AreEqual("actual-current", observed.RemoteRevision); Assert.HasCount(1, fixture.Stats.Requests);
        CollectionAssert.AreEquivalent(observed.SelectedConflictIds.ToArray(), fixture.Center.Query().Select(row => row.ConflictId).ToArray());
        foreach (Guid previous in group.Conflicts) Assert.AreEqual(MirrorPulseConflictStatus.Dismissed, (await fixture.Catalog.ReadUploadConflictAsync(previous))!.Status);
        await fixture.ReopenAsync(); await fixture.Recovery().RepairAsync(default);
        Assert.HasCount(1, fixture.Stats.Requests);
        Assert.AreEqual(JsonSerializer.Serialize(observed), JsonSerializer.Serialize(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect)));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadMutationAsync(group.Effect)));
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("disabled-instance")]
    [DataRow("disabled-root")]
    [DataRow("closed-root")]
    [DataRow("foreign-route")]
    [DataRow("foreign-item")]
    public async Task UnavailableOrMismatchedRootsNeverReachTheSource(string boundary)
    {
        await using var fixture = await Fixture.OpenAsync(boundary == "disabled-root");
        var group = await fixture.PrepareAsync(foreignRoute: boundary == "foreign-route", foreignItem: boundary == "foreign-item");
        await using var work = boundary == "closed-root" ? new MirrorPulseNamespaceWorkAdmission([fixture.Root.RootId]) : null;
        await fixture.Recovery(boundary != "disabled-instance", work).RepairAsync(default);
        Assert.IsEmpty(fixture.Stats.Requests); Assert.IsNull(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect));
        Assert.HasCount(2, await fixture.Catalog.ReadUploadConflictsAsync());
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("failure")]
    public async Task UnavailableStatRetainsOriginalErrorFactsAndARealLaterObservationCanRecover(string boundary)
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync();
        fixture.Stats.Result = boundary == "empty" ? "" : null; fixture.Stats.Fail = boundary == "failure";
        var recovery = fixture.Recovery(); await recovery.RepairAsync(default);
        Assert.IsNull(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect));
        foreach (Guid id in group.Conflicts)
        {
            var conflict = (await fixture.Catalog.ReadUploadConflictAsync(id))!;
            Assert.IsTrue(conflict.IsPending); Assert.IsNull(conflict.LocalRevision); Assert.IsNull(conflict.RemoteRevision);
        }
        fixture.Stats.Result = "actual-later"; fixture.Stats.Fail = false;
        await recovery.RepairAsync(default);
        Assert.AreEqual("actual-later", (await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect))!.RemoteRevision);
        Assert.HasCount(2, fixture.Stats.Requests); Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        if (File.Exists(fixture.Log.FilePath)) Assert.IsFalse((await File.ReadAllTextAsync(fixture.Log.FilePath)).Contains("private-note", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationAfterStatCannotPublishAnObservationAndRecoveryUsesANewActualStat()
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync();
        using var cancel = new CancellationTokenSource(); fixture.Stats.AfterStat = () => cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Recovery().RepairAsync(cancel.Token));
        Assert.IsNull(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect)); Assert.HasCount(2, await fixture.Catalog.ReadUploadConflictsAsync());
        fixture.Stats.AfterStat = null; await fixture.Recovery().RepairAsync(default);
        Assert.IsNotNull(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect)); Assert.HasCount(2, fixture.Stats.Requests);
    }

    [TestMethod]
    public async Task RecoveryRotatesPastSixtyFourOriginalRowsWithoutRetryingTheFailedPrefix()
    {
        await using var fixture = await Fixture.OpenAsync();
        for (int index = 0; index < 65; index++) await fixture.PrepareAsync();
        fixture.Stats.Fail = true; var recovery = fixture.Recovery();
        await recovery.RepairAsync(default); Assert.HasCount(32, fixture.Stats.Requests);
        await recovery.RepairAsync(default); Assert.HasCount(64, fixture.Stats.Requests);
        await recovery.RepairAsync(default); Assert.HasCount(65, fixture.Stats.Requests);
        Assert.HasCount(65, fixture.Stats.Requests.Select(request => request.NormalizedPath).Distinct().ToArray());
        Assert.HasCount(130, await fixture.Catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        Assert.IsFalse((await File.ReadAllTextAsync(fixture.Log.FilePath)).Contains("private-note", StringComparison.Ordinal));
    }

    private sealed record Group(Guid Effect, Guid[] Conflicts);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-observation-recovery-" + Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths => new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal RootRegistration Root { get; private set; } = null!;
        internal MirrorPulseRootRouter Router { get; private set; } = null!;
        internal MirrorPulseConflictCenter Center { get; } = new();
        internal StatTransport Stats { get; } = new();
        internal LocalRollingLogWriter Log { get; private set; } = null!;
        private readonly MirrorPulseInstanceScheduler _scheduler = new();

        internal static async Task<Fixture> OpenAsync(bool disabledRoot = false)
        {
            var fixture = new Fixture();
            fixture.Root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.observation-recovery"), InstanceId.New(),
                new("docs", "Docs", "Docs", false), disabledRoot ? RootRegistrationState.Disabled : RootRegistrationState.Active);
            fixture.Router = new(fixture.Paths.SyncRootPath, [fixture.Root]); fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            fixture.Log = new(Path.Combine(fixture.Paths.DataRootPath, "logs")); return fixture;
        }

        internal MirrorPulseConflictRemoteObservationRecovery Recovery(bool enabled = true, MirrorPulseNamespaceWorkAdmission? work = null) =>
            new(Catalog, Router, Stats, _ => enabled, _scheduler, Log, Center, namespaceWork: work);

        internal async Task<Group> PrepareAsync(bool foreignRoute = false, bool foreignItem = false)
        {
            Guid item = Guid.NewGuid(); string path = item.ToString("N") + ".txt";
            MirrorPulseWorkerChangeCommand[] window = [new(Guid.NewGuid(), 1, Root.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                path, null, null, false, item, DateTimeOffset.UtcNow), new(Guid.NewGuid(), 2, Root.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
                path, null, null, false, item, DateTimeOffset.UtcNow)];
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
            byte[] bytes = [1, 2, 3];
            await Catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, "original-request-baseline", new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.None)));
            var effect = await Catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
            await Catalog.TransitionMutationAsync(effect.Intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await Catalog.TransitionMutationAsync(effect.Intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
            var conflicts = new List<Guid>();
            foreach (var member in plan.Members)
            {
                Guid id = Guid.NewGuid(); conflicts.Add(id);
                var conflict = new MirrorPulseConflictRecord(id, Root.InstanceId, member.OperationId.ToString("D"), (foreignRoute ? "Foreign/" : "Docs/") + (foreignItem ? "another-" : "") + path,
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, null, null, DateTimeOffset.UtcNow);
                await Catalog.SaveUploadConflictAsync(conflict); Center.Upsert(conflict);
            }
            return new(effect.Intent.OperationId, conflicts.ToArray());
        }

        internal async Task ReopenAsync() { await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths); }
        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync(); await _scheduler.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory));
                string name = Path.GetFileName(full); const string prefix = "MirrorPulse-observation-recovery-";
                if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refuse cleanup outside the exact observation recovery fixture.");
                Directory.Delete(full, recursive: true);
            }
        }
    }

    private sealed class StatTransport : IMirrorPulseWorkerStatTransport
    {
        internal List<MirrorPulseWorkerStatRequest> Requests { get; } = [];
        internal string? Result { get; set; } = "actual-current";
        internal bool Fail { get; set; }
        internal Action? AfterStat { get; set; }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); AfterStat?.Invoke();
            if (Fail) throw new IOException("private-note source failure");
            return ValueTask.FromResult(Result);
        }
    }
}
