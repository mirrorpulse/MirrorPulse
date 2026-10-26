using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRemoteConflictActionCatalogTests
{
    [TestMethod]
    [DataRow("command")]
    [DataRow("receipt")]
    public async Task TheObservedReturnAndCommandTerminalCommitInOneActualTransaction(string boundary)
    {
        using var fixture = new Fixture(); var intent = Intent(); var receipt = Receipt(intent);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.IsNull(await catalog.PrepareRemoteConflictActionAsync(intent));
            await fixture.ExecuteAsync(boundary == "command"
                ? "CREATE TRIGGER reject_result BEFORE UPDATE OF state ON user_commands WHEN NEW.state='resolved' BEGIN SELECT RAISE(FAIL,'Synthetic terminal write loss'); END;"
                : "CREATE TRIGGER reject_result BEFORE UPDATE OF outcome_payload ON remote_conflict_actions BEGIN SELECT RAISE(FAIL,'Synthetic observed return write loss'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordRemoteConflictActionReceiptAsync(receipt));
            Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(intent.CommandId))!.State);
            Assert.AreEqual(1L, await fixture.ScalarAsync("SELECT count(*) FROM remote_conflict_actions WHERE outcome_payload IS NULL AND outcome_fingerprint IS NULL;"));
            await fixture.ExecuteAsync("DROP TRIGGER reject_result;");
            await catalog.RecordRemoteConflictActionReceiptAsync(receipt);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(receipt, await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
            Assert.AreEqual(receipt, await catalog.PrepareRemoteConflictActionAsync(intent));
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(intent.CommandId))!.State);
            Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
    }

    [TestMethod]
    [DataRow("intent-hash")]
    [DataRow("outcome-hash")]
    [DataRow("command-state")]
    [DataRow("command-target")]
    public async Task CorruptOrUncorrelatedReceiptCannotBecomeACompletedReplay(string changed)
    {
        using var fixture = new Fixture(); var intent = Intent();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            _ = await catalog.PrepareRemoteConflictActionAsync(intent);
            await catalog.RecordRemoteConflictActionReceiptAsync(Receipt(intent));
        }
        await fixture.ExecuteAsync(changed switch
        {
            "intent-hash" => "UPDATE remote_conflict_actions SET fingerprint=zeroblob(32);",
            "outcome-hash" => "UPDATE remote_conflict_actions SET outcome_fingerprint=zeroblob(32);",
            "command-state" => "UPDATE user_commands SET state='failed';",
            _ => "UPDATE user_commands SET target_id='another-conflict';",
        });
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRemoteConflictActionAsync(intent));
        }
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("failed")]
    [DataRow("superseded")]
    public async Task AGenericCommandWriteCannotResetAnObservedRemoteTerminal(string state)
    {
        using var fixture = new Fixture(); var intent = Intent(); var receipt = Receipt(intent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        _ = await catalog.PrepareRemoteConflictActionAsync(intent);
        await catalog.RecordRemoteConflictActionReceiptAsync(receipt);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveUserCommandAsync(
            new(intent.CommandId, "keepremote", intent.Conflict.ConflictId.ToString("D"), state)));
        Assert.AreEqual(receipt, await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(intent.CommandId))!.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnAdmittedFailedDecisionCannotSubstituteAnotherChoice(bool changeSide)
    {
        using var fixture = new Fixture(); var intent = Intent();
        if (changeSide) intent = intent with { Action = MirrorPulseConflictAction.KeepBoth, PreservedSide = MirrorPulseConflictPreservedSide.Local };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        _ = await catalog.PrepareRemoteConflictActionAsync(intent);
        string action = changeSide ? "keep-both-local" : "keepremote";
        await catalog.SaveUserCommandAsync(new(intent.CommandId, action, intent.Conflict.ConflictId.ToString("D"), "failed"));
        var changed = changeSide ? intent with { PreservedSide = MirrorPulseConflictPreservedSide.Remote }
            : intent with { Action = MirrorPulseConflictAction.KeepLocal };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRemoteConflictActionAsync(changed));
        Assert.AreEqual("failed", (await catalog.ReadUserCommandAsync(intent.CommandId))!.State);
        Assert.IsNull(await catalog.PrepareRemoteConflictActionAsync(intent));
        Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
    }

    [TestMethod]
    public async Task AnObservedResultIsImmutableAndRequiresItsOriginalAdmission()
    {
        using var fixture = new Fixture(); var intent = Intent(); var receipt = Receipt(intent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordRemoteConflictActionReceiptAsync(receipt));
        _ = await catalog.PrepareRemoteConflictActionAsync(intent);
        await catalog.RecordRemoteConflictActionReceiptAsync(receipt);
        await catalog.RecordRemoteConflictActionReceiptAsync(receipt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordRemoteConflictActionReceiptAsync(
            receipt with { Status = MirrorPulseRemoteConflictObservedStatus.AlreadyApplied }));
        Assert.AreEqual(receipt, await catalog.ReadRemoteConflictActionReceiptAsync(intent.CommandId));
    }

    [TestMethod]
    public async Task UpgradingSchema76CannotInventAReceiptForAHistoricalResolvedCommand()
    {
        using var fixture = new Fixture(); var intent = Intent();
        var command = new MirrorPulseUserCommandRecord(intent.CommandId, "keepremote", intent.Conflict.ConflictId.ToString("D"), "resolved");
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) await catalog.SaveUserCommandAsync(command);
        await fixture.ExecuteAsync("DROP TABLE remote_conflict_actions; PRAGMA user_version=76;");
        for (int owner = 0; owner < 2; owner++)
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            Assert.IsNull(await catalog.ReadRemoteConflictActionReceiptAsync(command.CommandId));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareRemoteConflictActionAsync(intent));
            Assert.AreEqual(command, await catalog.ReadUserCommandAsync(command.CommandId));
            Assert.AreEqual(0L, await fixture.ScalarAsync("SELECT count(*) FROM remote_conflict_actions;"));
            Assert.AreEqual(92L, await fixture.ScalarAsync("PRAGMA user_version;"));
        }
    }

    private static MirrorPulseRemoteConflictActionIntent Intent() => new(1, Guid.NewGuid(), new(Guid.NewGuid(), InstanceId.New(),
        "original-change", "Docs/original.txt", MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged,
        "local-v1", "remote-v2", DateTimeOffset.UtcNow, source: MirrorPulseConflictSource.CfSharpRemote), MirrorPulseConflictAction.KeepRemote,
        null, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
    private static MirrorPulseRemoteConflictActionReceipt Receipt(MirrorPulseRemoteConflictActionIntent intent) =>
        new(intent, MirrorPulseRemoteConflictObservedStatus.Applied, null, DateTimeOffset.UtcNow);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-remote-receipt-catalog", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public Fixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync();
        }
        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
