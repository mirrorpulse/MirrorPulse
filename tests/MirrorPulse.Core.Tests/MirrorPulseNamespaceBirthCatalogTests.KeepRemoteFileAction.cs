using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteFileActionRetainsFirstPermissionIntentAcrossCatalogOwnersWithoutApplyingOrAcknowledging()
    {
        using var fixture = new Fixture(); MirrorPulseConflictKeepRemoteFileAction action; string startJson, planJson;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
            action = KeepRemoteAction(start); startJson = JsonSerializer.Serialize(start);
            planJson = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(setup.Preparation.Intent.PlanId));
            Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(start.CommandId));
            Assert.AreEqual(action, await catalog.RetainConflictKeepRemoteFileActionAsync(action));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(action, await reopened.ReadConflictKeepRemoteFileActionAsync(action.CommandId));
        Assert.AreEqual(action, await reopened.RetainConflictKeepRemoteFileActionAsync(action));
        var original = (await reopened.ReadConflictKeepRemoteFileStartAsync(action.CommandId))!;
        Assert.AreEqual(startJson, JsonSerializer.Serialize(original));
        var preparation = (await reopened.ReadConflictKeepRemoteAsync(action.CommandId))!;
        Assert.AreEqual(planJson, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(preparation.Intent.PlanId)));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(action.CommandId))!.State);
        Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
        CollectionAssert.AreEqual(new[] { original.Original.Context.Member.RootId!.Value }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("object")]
    [DataRow("owner")]
    [DataRow("role")]
    [DataRow("seal")]
    [DataRow("restore")]
    [DataRow("time")]
    public async Task KeepRemoteFileActionCannotReplaceItsFirstIdentityPolicyRoleOrTime(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
        var action = KeepRemoteAction(start); await catalog.RetainConflictKeepRemoteFileActionAsync(action);
        var modified = changed switch
        {
            "action" => action with { ActionId = Guid.NewGuid() },
            "object" => action with { LocalObject = action.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "owner" => action with { OwnerSid = "S-1-5-21-1-2-3-2000" },
            "role" => action with { RoleSid = "S-1-5-5-700-800" },
            "seal" => action with { RoleOnlyDacl = Dacl("D:P(A;;FR;;;SY)") },
            "restore" => action with { RestoreDacl = Dacl("D:P(A;;FR;;;SY)") },
            _ => action with { PreparedAt = action.PreparedAt.AddTicks(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RetainConflictKeepRemoteFileActionAsync(modified));
        Assert.AreEqual(action, await catalog.ReadConflictKeepRemoteFileActionAsync(action.CommandId));
    }

    [TestMethod]
    public async Task KeepRemoteFileActionCannotPrecedeItsCompleteOriginalFileStart()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog);
        var provisional = new MirrorPulseConflictKeepRemoteFileStart(1, setup.Preparation.Intent.CommandId, setup.StoreVolume,
            setup.StoreFileId, new string('A', 64), setup.Original, setup.Parent, setup.StartedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RetainConflictKeepRemoteFileActionAsync(KeepRemoteAction(provisional)));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(provisional.CommandId));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("conflict_keep_remote_file_actions")]
    [DataRow("conflict_keep_remote_file_action_owners")]
    public async Task KeepRemoteFileActionInsertionFaultRollsBackBothRowsAndPreservesThePendingStart(string table)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
        var action = KeepRemoteAction(start);
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_action BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RetainConflictKeepRemoteFileActionAsync(action));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(action.CommandId));
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteFileStartAsync(action.CommandId)));
        CollectionAssert.AreEqual(new[] { start.Original.Context.Member.RootId!.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_action;");
        Assert.AreEqual(action, await catalog.RetainConflictKeepRemoteFileActionAsync(action));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("owner")]
    [DataRow("fingerprint")]
    public async Task KeepRemoteFileActionCorruptionCannotBecomeAFreshPermissionDecision(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
        var action = KeepRemoteAction(start); await catalog.RetainConflictKeepRemoteFileActionAsync(action);
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "action" => "PRAGMA foreign_keys=OFF; DELETE FROM conflict_keep_remote_file_actions;",
            "owner" => "DELETE FROM conflict_keep_remote_file_action_owners;",
            _ => "UPDATE conflict_keep_remote_file_actions SET fingerprint=zeroblob(32);",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteFileActionAsync(action.CommandId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RetainConflictKeepRemoteFileActionAsync(action));
        CollectionAssert.AreEqual(new[] { start.Original.Context.Member.RootId!.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task CancelledKeepRemoteFileActionLeavesTheOriginalStartAndFencePending()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => catalog.RetainConflictKeepRemoteFileActionAsync(KeepRemoteAction(start), stop.Token));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(start.CommandId));
        CollectionAssert.AreEqual(new[] { start.Original.Context.Member.RootId!.Value }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("owner")]
    [DataRow("definition")]
    public async Task Schema97RefusesMissingOrChangedKeepRemotePermissionOwnership(string changed)
    {
        using var fixture = new Fixture(); await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "action" => "PRAGMA foreign_keys=OFF; DROP TABLE conflict_keep_remote_file_actions;",
            "owner" => "DROP TABLE conflict_keep_remote_file_action_owners;",
            _ => "ALTER TABLE conflict_keep_remote_file_actions ADD COLUMN invented INTEGER;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    [TestMethod]
    public async Task Schema96UpgradesPermissionOwnershipWithoutInventingAnActionForAPendingStart()
    {
        using var fixture = new Fixture(); MirrorPulseConflictKeepRemoteFileStart start;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteFileAsync(catalog); start = await StartKeepRemoteFileAsync(catalog, setup);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_file_action_owners; DROP TABLE conflict_keep_remote_file_actions; PRAGMA user_version=96;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await reopened.ReadConflictKeepRemoteFileActionAsync(start.CommandId));
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteFileStartAsync(start.CommandId)));
        var action = KeepRemoteAction(start);
        Assert.AreEqual(action, await reopened.RetainConflictKeepRemoteFileActionAsync(action));
    }

    [TestMethod]
    public async Task DowngradedSchema96CannotAdoptExistingKeepRemotePermissionHistory()
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteFileAsync(catalog); var start = await StartKeepRemoteFileAsync(catalog, setup);
            await catalog.RetainConflictKeepRemoteFileActionAsync(KeepRemoteAction(start));
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=96;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    private static MirrorPulseConflictKeepRemoteFileAction KeepRemoteAction(MirrorPulseConflictKeepRemoteFileStart start) =>
        new(1, start.CommandId, Guid.NewGuid(), start.Original.Context.Member.LocalObject,
            start.Original.Context.Member.OwnerSid, start.Original.Context.Protection.RoleSid,
            Dacl($"D:P(A;;FA;;;{start.Original.Context.Protection.RoleSid})"), start.Original.ExpectedDacl, start.StartedAt.AddSeconds(1));
}
