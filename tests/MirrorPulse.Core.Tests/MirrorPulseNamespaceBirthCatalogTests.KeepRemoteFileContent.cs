using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteContentFactsSurviveBothCatalogOwnersAndLeaveOriginalsPending()
    {
        using var fixture = new Fixture(); KeepRemoteContentSetup setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareKeepRemoteContentAsync(catalog);
            Assert.AreEqual(setup.Write, await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write));
            Assert.AreEqual(setup.Completion, await catalog.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = await reopened.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId);
        Assert.IsNotNull(actual);
        Assert.AreEqual(setup.Action, actual.Action);
        Assert.AreEqual(setup.Write, actual.WriteStart);
        Assert.AreEqual(setup.Completion, actual.Completion);
        Assert.AreEqual(setup.Write, await reopened.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write));
        Assert.AreEqual(setup.Completion, await reopened.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(setup.Action.CommandId))!.State);
        Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
        Assert.HasCount(1, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task KeepRemoteContentCannotCompleteBeforeTheOriginalWriteStart()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion));
        var actual = await catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId);
        Assert.IsNotNull(actual);
        Assert.IsNull(actual.WriteStart);
        Assert.IsNull(actual.Completion);
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("file")]
    [DataRow("parent")]
    [DataRow("seal")]
    [DataRow("identity")]
    [DataRow("time")]
    public async Task KeepRemoteWriteStartCannotReplaceItsFirstObjectPolicyOrIdentity(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog);
        await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        var value = changed switch
        {
            "action" => setup.Write with { ActionId = Guid.NewGuid() },
            "file" => setup.Write with { Child = setup.Write.Child with { LocalObject = setup.Write.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent" => setup.Write with { Parent = setup.Write.Parent with { LocalObject = setup.Write.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "seal" => setup.Write with { Child = setup.Write.Child with { Dacl = setup.Action.RestoreDacl } },
            "identity" => setup.Write with { OriginalPlaceholderIdentity = "BAUG" },
            _ => setup.Write with { StartedAt = setup.Write.StartedAt.AddTicks(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteFileWriteStartAsync(value));
        Assert.AreEqual(setup.Write, (await catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId))!.WriteStart);
    }

    [TestMethod]
    [DataRow("copy")]
    [DataRow("file")]
    [DataRow("parent")]
    [DataRow("seal")]
    [DataRow("identity")]
    [DataRow("link")]
    [DataRow("time")]
    [DataRow("action")]
    public async Task KeepRemoteContentCompletionRequiresTheSelectedBytesAndOriginalSeal(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog); await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        var value = changed switch
        {
            "copy" => setup.Completion with { Copy = setup.Completion.Copy with { Sha256 = new string('A', 64) } },
            "file" => setup.Completion with { Child = setup.Completion.Child with { LocalObject = setup.Completion.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "parent" => setup.Completion with { Parent = setup.Completion.Parent with { LocalObject = setup.Completion.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "seal" => setup.Completion with { Child = setup.Completion.Child with { Dacl = setup.Action.RestoreDacl } },
            "identity" => setup.Completion with { OriginalPlaceholderIdentity = "BAUG" },
            "link" => setup.Completion with { LinkCount = 2 },
            "time" => setup.Completion with { VerifiedAt = setup.Write.StartedAt.AddTicks(-1) },
            _ => setup.Completion with { ActionId = Guid.NewGuid() },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteFileContentCompletionAsync(value));
        Assert.IsNull((await catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId))!.Completion);
    }

    [TestMethod]
    [DataRow(1, "conflict_keep_remote_content_facts")]
    [DataRow(1, "conflict_keep_remote_content_owners")]
    [DataRow(2, "conflict_keep_remote_content_facts")]
    [DataRow(2, "conflict_keep_remote_content_owners")]
    public async Task KeepRemoteContentInsertInterruptionRollsBackItsFactAndOwnershipTogether(int kind, string table)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog);
        if (kind == 2) await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        string before = JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId));
        await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_content BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        Task RecordAsync() => kind == 1 ? catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write)
            : catalog.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion);
        await Assert.ThrowsExactlyAsync<SqliteException>(RecordAsync);
        Assert.AreEqual(before, JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId)));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_content;");
        await RecordAsync();
    }

    [TestMethod]
    [DataRow(1, "facts")]
    [DataRow(1, "owners")]
    [DataRow(2, "facts")]
    [DataRow(2, "owners")]
    [DataRow(2, "hash")]
    public async Task KeepRemoteContentReadRefusesPartialOrCorruptRetainedHistory(int kind, string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareKeepRemoteContentAsync(catalog);
        await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        await catalog.RecordConflictKeepRemoteFileContentCompletionAsync(setup.Completion);
        await ExecuteSqlAsync(fixture.Paths, changed == "hash"
            ? $"UPDATE conflict_keep_remote_content_facts SET fingerprint=zeroblob(32) WHERE kind={kind};"
            : $"DELETE FROM conflict_keep_remote_content_{changed} WHERE kind={kind};");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId));
        Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema97KeepsAnExistingPermissionActionWithoutInventingContentWork()
    {
        using var fixture = new Fixture(); KeepRemoteContentSetup setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            setup = await PrepareKeepRemoteContentAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE conflict_keep_remote_content_owners; DROP TABLE conflict_keep_remote_content_facts; PRAGMA user_version=97;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = await reopened.ReadConflictKeepRemoteFileContentAsync(setup.Action.CommandId);
        Assert.IsNotNull(actual);
        Assert.AreEqual(setup.Action, actual.Action);
        Assert.IsNull(actual.WriteStart);
        Assert.IsNull(actual.Completion);
    }

    [TestMethod]
    [DataRow("facts")]
    [DataRow("owners")]
    [DataRow("definition")]
    [DataRow("downgraded")]
    public async Task Schema98RefusesMissingChangedOrDowngradedKeepRemoteContentHistory(string changed)
    {
        using var fixture = new Fixture();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareKeepRemoteContentAsync(catalog);
            await catalog.RecordConflictKeepRemoteFileWriteStartAsync(setup.Write);
        }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "facts" => "DROP TABLE conflict_keep_remote_content_facts;",
            "owners" => "DROP TABLE conflict_keep_remote_content_owners;",
            "definition" => "ALTER TABLE conflict_keep_remote_content_facts ADD COLUMN invented INTEGER;",
            _ => "PRAGMA user_version=97;",
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
    }

    private sealed record KeepRemoteContentSetup(MirrorPulseConflictKeepRemoteFileAction Action,
        MirrorPulseConflictKeepRemoteFileWriteStart Write, MirrorPulseConflictKeepRemoteFileContentCompletion Completion);

    private static async Task<KeepRemoteContentSetup> PrepareKeepRemoteContentAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareKeepRemoteFileAsync(catalog); var first = await StartKeepRemoteFileAsync(catalog, setup);
        var action = KeepRemoteAction(first); await catalog.RetainConflictKeepRemoteFileActionAsync(action);
        var child = new MirrorPulseNamespacePermissionVerification(action.LocalObject, action.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(1));
        var parent = new MirrorPulseNamespacePermissionVerification(first.Parent.Context.Member.LocalObject,
            first.Parent.Context.Member.OwnerSid, first.Parent.ExpectedDacl, child.ObservedAt);
        string identity = setup.Preparation.Intent.LocalContent!.UploadBinding.PreviousPlaceholderIdentity!;
        var write = new MirrorPulseConflictKeepRemoteFileWriteStart(1, action.CommandId, action.ActionId,
            child, parent, 1, identity, child.ObservedAt.AddSeconds(1));
        var complete = new MirrorPulseConflictKeepRemoteFileContentCompletion(1, action.CommandId, action.ActionId, setup.Preparation.RemoteCopy!,
            child with { ObservedAt = write.StartedAt.AddSeconds(1) }, parent with { ObservedAt = write.StartedAt.AddSeconds(1) },
            1, identity, write.StartedAt.AddSeconds(2));
        return new(action, write, complete);
    }
}
