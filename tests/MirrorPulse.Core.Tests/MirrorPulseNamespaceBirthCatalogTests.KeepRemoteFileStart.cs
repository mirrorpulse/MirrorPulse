using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteFileStartRetainsFirstFactsAcrossOwnersWithoutApplicationOrAcknowledgement()
    {
        using var fixture = new Fixture(); KeepRemoteFileSetup setup; MirrorPulseConflictKeepRemoteFileStart start;
        string rejected, execution;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteFileAsync(catalog);
            rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(setup.Preparation.Intent.RejectedEffect.OperationId));
            execution = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId));
            Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
            start = await StartKeepRemoteFileAsync(catalog, setup);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteFileStartAsync(start.CommandId)));
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await StartKeepRemoteFileAsync(reopened, setup)));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await reopened.ReadMutationAsync(setup.Preparation.Intent.RejectedEffect.OperationId)));
        Assert.AreEqual(execution, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId)));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(start.CommandId))!.State);
        Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
        CollectionAssert.AreEqual(new[] { setup.Original.Context.Member.RootId!.Value }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("store-file")]
    [DataRow("store-volume")]
    [DataRow("time")]
    [DataRow("child-descriptor")]
    [DataRow("parent-descriptor")]
    [DataRow("location")]
    public async Task KeepRemoteFileStartCannotReplaceItsFirstStoreObjectsRoleOrTime(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); var original = await StartKeepRemoteFileAsync(catalog, setup);
        var modified = changed switch
        {
            "store-file" => setup with { StoreFileId = Guid.NewGuid() },
            "store-volume" => setup with { StoreVolume = setup.StoreVolume + 1 },
            "time" => setup with { StartedAt = setup.StartedAt.AddTicks(1) },
            "child-descriptor" => setup with { Original = setup.Original with { ExpectedDacl = "another descriptor" } },
            "parent-descriptor" => setup with { Parent = setup.Parent with { ExpectedDacl = "another descriptor" } },
            _ => setup with
            {
                Original = setup.Original with
                {
                    Context = setup.Original.Context with
                    { Location = setup.Original.Context.Location with { RelativePath = setup.Original.Context.Location.RelativePath + ".changed" } }
                }
            },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartKeepRemoteFileAsync(catalog, modified));
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteFileStartAsync(original.CommandId)));
    }

    [TestMethod]
    [DataRow("object")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("parent")]
    public async Task KeepRemoteFileStartRejectsAnotherObjectIdentityOrDirectParent(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog);
        var modified = changed switch
        {
            "object" => setup with
            {
                Original = setup.Original with
                {
                    Context = setup.Original.Context with
                    { Member = setup.Original.Context.Member with { LocalObject = setup.Original.Context.Member.LocalObject with { LocalFileId = Guid.NewGuid() } } }
                }
            },
            "item" => setup with { Original = setup.Original with { Identity = setup.Original.Identity with { ItemId = Guid.NewGuid() } } },
            "remote" => setup with { Original = setup.Original with { Identity = setup.Original.Identity with { RemoteId = "another-worker-object" } } },
            _ => setup with { Parent = setup.Parent with { Context = setup.Parent.Context with { Member = setup.Parent.Context.Member with { MemberId = Guid.NewGuid() } } } },
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => StartKeepRemoteFileAsync(catalog, modified));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
    }

    [TestMethod]
    public async Task KeepRemoteFileStartNeedsAllOriginalJournalCorrelationsBeforeItCanCommit()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog, retainJournal: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
    }

    [TestMethod]
    [DataRow("conflict_keep_remote_file_starts")]
    [DataRow("conflict_keep_remote_file_pending")]
    public async Task KeepRemoteFileStartInsertFaultRollsBackAndReplaysOnlyTheOriginalFacts(string table)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER fail_file_start BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'file start interrupted'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => StartKeepRemoteFileAsync(catalog, setup));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(setup.Preparation.Intent.CommandId))!.State);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_file_start;");
        Assert.IsNotNull(await StartKeepRemoteFileAsync(catalog, setup));
    }

    [TestMethod]
    public async Task CancelledKeepRemoteFileStartCannotInventFirstFacts()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => StartKeepRemoteFileAsync(catalog, setup, stop.Token));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("missing-pending")]
    [DataRow("missing-index")]
    [DataRow("definition")]
    [DataRow("old-history")]
    public async Task Schema96RefusesMissingChangedOrInventedKeepRemoteFileStarts(string changed)
    {
        using var fixture = new Fixture(); await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        if (changed == "missing") await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_file_starts;");
        else if (changed == "missing-pending") await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_file_pending;");
        else if (changed == "missing-index") await ExecuteSqlAsync(fixture.Paths, "DROP INDEX ix_keep_remote_file_start_root;");
        else if (changed == "definition") await ExecuteSqlAsync(fixture.Paths, "ALTER TABLE conflict_keep_remote_file_starts ADD COLUMN invented INTEGER;");
        else
        {
            await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; INSERT INTO conflict_keep_remote_file_starts VALUES('invented','invented','invented',X'01',zeroblob(32)); PRAGMA user_version=95;");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    [TestMethod]
    public async Task Schema95UpgradesToAnEmptyKeepRemoteFileStartTable()
    {
        using var fixture = new Fixture(); await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_file_pending; DROP TABLE conflict_keep_remote_file_starts; PRAGMA user_version=95;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await reopened.ReadConflictKeepRemoteFileStartAsync(Guid.NewGuid()));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(101L, await query.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task KeepRemoteFileStartFencesCompetingNamespaceReplacementBeforeAnyPermissionAction()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); await StartKeepRemoteFileAsync(catalog, setup);
        var replacement = Replacement(setup.Original);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementAsync(replacement));
        Assert.IsNull(await catalog.ReadNamespaceReplacementAsync(replacement.OperationId));
        Assert.AreEqual(setup.Original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Original.Context.Member.MemberId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KeepRemoteFileStartMissingEitherOwnershipRowCannotSilentlyReleaseItsRootFence(bool removeStart)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); await StartKeepRemoteFileAsync(catalog, setup);
        await ExecuteSqlAsync(fixture.Paths, removeStart
            ? "PRAGMA foreign_keys=OFF; DELETE FROM conflict_keep_remote_file_starts;"
            : "DELETE FROM conflict_keep_remote_file_pending;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteFileStartAsync(setup.Preparation.Intent.CommandId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceReplacementAsync(Replacement(setup.Original)));
    }

    private sealed record KeepRemoteFileSetup(MirrorPulseConflictKeepRemotePreparation Preparation,
        MirrorPulseNamespaceMemberProtectionSnapshot Original, MirrorPulseNamespaceMemberProtectionSnapshot Parent,
        ulong StoreVolume, Guid StoreFileId, DateTimeOffset StartedAt);

    private static Task<MirrorPulseConflictKeepRemoteFileStart> StartKeepRemoteFileAsync(MirrorPulseProductCatalog catalog,
        KeepRemoteFileSetup setup, CancellationToken token = default) => catalog.RetainConflictKeepRemoteFileStartAsync(
            setup.Preparation.Intent.CommandId, setup.StoreVolume, setup.StoreFileId, setup.Original, setup.Parent, setup.StartedAt, token);

    private static async Task<KeepRemoteFileSetup> PrepareKeepRemoteFileAsync(MirrorPulseProductCatalog catalog, bool retainJournal = true)
    {
        var member = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, member.Move.RootId);
        var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.Member.MemberId))!;
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Location.ParentMemberId!.Value))!;
        string path = original.Context.Location.RelativePath[(root.Label.Length + 1)..];
        byte[] bytes = [1, 2, 3, 4]; string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var content = new MirrorPulseCoalescingContent(bytes.Length, hash,
            new(original.Context.Member.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, "AQID"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MirrorPulseWorkerChangeCommand[] window = Enumerable.Range(1, 2).Select(sequence => new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), sequence,
            root.InstanceId, root.UniquenessKey, MirrorPulseWorkerChangeKind.ContentUpdate, path, null, null, false,
            original.Identity.ItemId!.Value, now.AddTicks(sequence))).ToArray();
        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, original.Identity.ItemId!.Value, 100, new HashSet<Guid>())!;
        await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, "baseline", content);
        var rejected = await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => new MirrorPulseMutationExecutor(catalog).ExecutePreparedAsync(rejected,
            _ => ValueTask.FromException<string?>(new MirrorPulseWorkerMutationConflictException("baseline", "selected")),
            (_, _) => ValueTask.CompletedTask, CancellationToken.None).AsTask());
        Guid conflict = Guid.Empty;
        foreach (var row in plan.Members)
        {
            Guid id = Guid.NewGuid(); if (conflict == Guid.Empty) conflict = id;
            await catalog.SaveUploadConflictAsync(new(id, root.InstanceId, row.OperationId.ToString("D"), path,
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "baseline", "selected", DateTimeOffset.UtcNow));
        }
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "keepremote", conflict.ToString("D"), "pending"));
        var preparation = await catalog.PrepareConflictKeepRemoteAsync(command, conflict, plan.ItemId, path,
            new(1, root.InstanceId.ToString(), root.UniquenessKey, path, original.Identity.RemoteId!, "selected", bytes.Length), content);
        var copy = new MirrorPulseConflictKeepBothCopy(bytes.Length, hash);
        await catalog.RecordConflictKeepRemoteLocalPreservationAsync(command, new(copy)); await catalog.RecordConflictKeepRemoteRemoteCopyAsync(command, copy);
        preparation = (await catalog.ReadConflictKeepRemoteAsync(command))!;
        if (retainJournal)
        {
            // Synthetic Core correlations exercise product durability; no SDK/native/byte authority is asserted.
            await catalog.RetainConflictKeepRemoteJournalAsync(new(1, command, plan.PlanId, plan.ItemId,
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(preparation.Intent))), new(copy), copy,
                plan.Members.Select(row => new MirrorPulseConflictKeepRemoteJournalMember(row.OperationId, row.Sequence, plan.ItemId, hash)).ToArray(), DateTimeOffset.UtcNow));
        }
        return new(preparation, original, parent, 42, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }
}
