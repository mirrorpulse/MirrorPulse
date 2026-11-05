using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseConflictKeepRemoteJournalTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task RetainedJournalCorrelationsSurviveBothOwnersWithoutApplyingOrAcknowledgingAnyEffect(MirrorPulseCoalescedEffect effect)
    {
        await using var fixture = await Fixture.OpenAsync(effect); string before = await fixture.RowsAsync(); int calls = fixture.Source.Calls;
        var retained = await fixture.Guard().RetainAsync(fixture.Copies); string original = JsonSerializer.Serialize(retained);
        CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), retained.Originals.Select(row => row.OperationId).ToArray());
        Assert.HasCount(fixture.Plan.Members.Count, retained.Originals);
        await fixture.ReopenOwnersAsync();
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Guard().RevalidateRetainedAsync(fixture.Copies)));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Guard().RetainAsync(fixture.Copies)));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.RetainConflictKeepRemoteJournalAsync(retained)));
        Assert.AreEqual(before, await fixture.RowsAsync()); Assert.AreEqual(calls, fixture.Source.Calls);
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(retained.CommandId))!.State);
        Assert.HasCount(fixture.Plan.Members.Count, await fixture.Catalog.ReadUploadConflictsAsync());
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("kind")]
    [DataRow("created")]
    [DataRow("attempt")]
    [DataRow("retry")]
    [DataRow("item")]
    [DataRow("missing")]
    [DataRow("sequence")]
    [DataRow("successor")]
    public async Task AReopenedJournalBindingRefusesEveryChangedOriginalAndNeverRecapturesIt(string change)
    {
        await using var fixture = await Fixture.OpenAsync();
        var retained = await fixture.Guard().RetainAsync(fixture.Copies); string original = JsonSerializer.Serialize(retained);
        await fixture.ChangeRowAsync(change); string changed = await fixture.RowsAsync(); await fixture.ReopenOwnersAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RevalidateRetainedAsync(fixture.Copies));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RetainAsync(fixture.Copies));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(retained.CommandId)));
        Assert.AreEqual(changed, await fixture.RowsAsync());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(retained.CommandId))!.State);
        Assert.HasCount(fixture.Plan.Members.Count, await fixture.Catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task ARawJournalChangeAfterProductCommitRetainsTheOldBindingAndRefusesRecapture()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.DuringRead = () => fixture.PublicReads == 2 ? fixture.ChangeRowAsync("payload") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RetainAsync(fixture.Copies));
        Guid command = fixture.Copies.Preparation.Intent.CommandId;
        var retained = await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(command); Assert.IsNotNull(retained);
        string original = JsonSerializer.Serialize(retained); string changed = await fixture.RowsAsync(); fixture.DuringRead = null;
        await fixture.ReopenOwnersAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RetainAsync(fixture.Copies));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(command)));
        Assert.AreEqual(changed, await fixture.RowsAsync()); Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
    }

    [TestMethod]
    public async Task OriginalRowRevalidationKeepsSuccessorsPendingAcrossBothStoreOwners()
    {
        await using var fixture = await Fixture.OpenAsync();
        var retained = await fixture.Guard().RetainAsync(fixture.Copies);
        string original = JsonSerializer.Serialize(retained);
        await fixture.ChangeRowAsync("successor");
        string pending = await fixture.RowsAsync();
        await fixture.ReopenOwnersAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RevalidateRetainedAsync(fixture.Copies));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Guard().RevalidateRetainedOriginalsAsync(fixture.Copies)));
        Assert.AreEqual(pending, await fixture.RowsAsync());
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(retained.CommandId))!.State);
        Assert.HasCount(fixture.Plan.Members.Count, await fixture.Catalog.ReadUploadConflictsAsync());
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("kind")]
    [DataRow("created")]
    [DataRow("attempt")]
    [DataRow("retry")]
    [DataRow("item")]
    [DataRow("missing")]
    [DataRow("sequence")]
    public async Task OriginalRowRevalidationStillRejectsChangedOrMissingOriginalsWhenASuccessorExists(string change)
    {
        await using var fixture = await Fixture.OpenAsync();
        var retained = await fixture.Guard().RetainAsync(fixture.Copies);
        string original = JsonSerializer.Serialize(retained);
        await fixture.ChangeRowAsync("successor");
        await fixture.ChangeRowAsync(change);
        string pending = await fixture.RowsAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Guard().RevalidateRetainedOriginalsAsync(fixture.Copies));
        Assert.AreEqual(pending, await fixture.RowsAsync());
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(retained.CommandId)));
    }

    [TestMethod]
    public async Task ARealBindingInsertFaultRollsBackWithoutConsumingAnyOriginal()
    {
        await using var fixture = await Fixture.OpenAsync(); string before = await fixture.RowsAsync();
        await using var connection = BindingConnection(fixture); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "CREATE TRIGGER refuse_journal_binding BEFORE INSERT ON conflict_keep_remote_journal_bindings BEGIN SELECT RAISE(ABORT,'original insert fault'); END;";
        await query.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Guard().RetainAsync(fixture.Copies));
        Assert.IsNull(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(fixture.Copies.Preparation.Intent.CommandId));
        Assert.AreEqual(before, await fixture.RowsAsync());
        query.CommandText = "DROP TRIGGER refuse_journal_binding;"; await query.ExecuteNonQueryAsync();
        Assert.IsNotNull(await fixture.Guard().RetainAsync(fixture.Copies)); Assert.AreEqual(before, await fixture.RowsAsync());
    }

    [TestMethod]
    public async Task CancelledRetentionCannotInventOrReplaceOriginalCorrelations()
    {
        await using var fixture = await Fixture.OpenAsync(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => fixture.Guard().RetainAsync(fixture.Copies, cancelled.Token));
        Guid command = fixture.Copies.Preparation.Intent.CommandId;
        Assert.IsNull(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(command)); Assert.AreEqual(0, fixture.PublicReads);
        var retained = await fixture.Guard().RetainAsync(fixture.Copies);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => fixture.Guard().RevalidateRetainedAsync(fixture.Copies, cancelled.Token));
        await fixture.ReopenOwnersAsync();
        Assert.AreEqual(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(command)));
    }

    [TestMethod]
    [DataRow("plan")]
    [DataRow("intent")]
    [DataRow("local-copy")]
    [DataRow("remote-copy")]
    [DataRow("observed")]
    [DataRow("row-hash")]
    [DataRow("sequence")]
    [DataRow("missing")]
    public async Task ProductBindingsCannotReplaceTheirSelectedDecisionCopiesOrExactOriginalSet(string changed)
    {
        await using var fixture = await Fixture.OpenAsync();
        var retained = await fixture.Guard().RetainAsync(fixture.Copies); string original = JsonSerializer.Serialize(retained);
        var replacement = changed switch
        {
            "plan" => retained with { PlanId = Guid.NewGuid() },
            "intent" => retained with { IntentSha256 = new string('A', 64) },
            "local-copy" => retained with { LocalPreservation = new(null) },
            "remote-copy" => retained with { RemoteCopy = retained.RemoteCopy with { Sha256 = new string('B', 64) } },
            "observed" => retained with { ObservedAt = retained.ObservedAt.AddTicks(1) },
            "row-hash" => retained with { Originals = retained.Originals.Select((row, index) => index == 0 ? row with { OriginalSha256 = new string('C', 64) } : row).ToArray() },
            "sequence" => retained with { Originals = retained.Originals.Select(row => row with { Sequence = row.Sequence + 100 }).ToArray() },
            _ => retained with { Originals = retained.Originals.Append(retained.Originals[^1] with { OperationId = Guid.NewGuid(), Sequence = long.MaxValue }).ToArray() }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.RetainConflictKeepRemoteJournalAsync(replacement));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(retained.CommandId)));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("definition")]
    [DataRow("downgraded-nonempty")]
    public async Task Schema95RefusesMissingChangedOrInventedJournalHistory(string damage)
    {
        await using var fixture = await Fixture.OpenAsync(); await fixture.Guard().RetainAsync(fixture.Copies);
        // Schema validation belongs to the next catalog owner, after the original
        // owner releases its exclusive lifetime. The original timeout is unchanged.
        await fixture.Catalog.DisposeAsync();
        await using var connection = BindingConnection(fixture); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = damage switch
        {
            "missing" => "DROP TABLE conflict_keep_remote_journal_bindings;",
            "definition" => "DROP TABLE conflict_keep_remote_journal_bindings; CREATE TABLE conflict_keep_remote_journal_bindings(command_id TEXT PRIMARY KEY,payload BLOB,fingerprint BLOB);",
            _ => "PRAGMA user_version=94;"
        };
        await query.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var refused = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
        query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(damage == "downgraded-nonempty" ? 94L : 101L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT state FROM user_commands WHERE command_id=$id;";
        query.Parameters.AddWithValue("$id", fixture.Copies.Preparation.Intent.CommandId.ToString("D"));
        Assert.AreEqual("pending", await query.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task Version94CopiesUpgradeWithoutInventingAnyRetainedJournalBinding()
    {
        await using var fixture = await Fixture.OpenAsync();
        string preparation = JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteAsync(fixture.Copies.Preparation.Intent.CommandId));
        string rows = await fixture.RowsAsync();
        await using (var connection = BindingConnection(fixture))
        {
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "DROP TABLE conflict_keep_remote_journal_bindings; PRAGMA user_version=94;"; await query.ExecuteNonQueryAsync();
        }
        await fixture.ReopenOwnersAsync();
        Assert.IsNull(await fixture.Catalog.ReadConflictKeepRemoteJournalAsync(fixture.Copies.Preparation.Intent.CommandId));
        Assert.AreEqual(preparation, JsonSerializer.Serialize(await fixture.Catalog.ReadConflictKeepRemoteAsync(fixture.Copies.Preparation.Intent.CommandId)));
        Assert.AreEqual(rows, await fixture.RowsAsync()); Assert.IsNotNull(await fixture.Guard().RetainAsync(fixture.Copies));
    }

    private static SqliteConnection BindingConnection(Fixture fixture) => new(new SqliteConnectionStringBuilder
    { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
}
