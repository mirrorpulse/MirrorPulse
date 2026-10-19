using System.Text;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteConflictActionsTests
{
    [TestMethod]
    [DataRow(MirrorPulseConflictAction.KeepRemote, null)]
    [DataRow(MirrorPulseConflictAction.KeepBoth, MirrorPulseConflictPreservedSide.Local)]
    [DataRow(MirrorPulseConflictAction.KeepBoth, MirrorPulseConflictPreservedSide.Remote)]
    public async Task AMissingSdkConflictCannotResolveAnUnprovenProductCommand(
        MirrorPulseConflictAction action, MirrorPulseConflictPreservedSide? side)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-recovery", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
        string localPath = Path.Combine(paths.SyncRootPath, "Docs", "note.txt");
        await File.WriteAllTextAsync(localPath, "retained local content");
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var conflict = Create("Docs/note.txt");
            await catalog.SaveRemoteConflictProjectionAsync(conflict);
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            int sdkCalls = 0;
            var actions = new MirrorPulseRemoteConflictActions((id, decision, _) =>
            {
                Assert.AreEqual(conflict.ConflictId, id);
                Assert.AreEqual(CloudRemoteConflictDecision.KeepRemote, decision);
                sdkCalls++;
                return ValueTask.FromException<CloudRemoteApplyEntryStatus>(new KeyNotFoundException("The original SDK conflict is unavailable."));
            }, new MirrorPulseConflictCopyStore(paths), catalog, center);
            Guid commandId = Guid.NewGuid();
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => actions.ApplyAsync(commandId, conflict, action, side,
                side == MirrorPulseConflictPreservedSide.Remote
                    ? _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("retained remote content"))) : null));
            Assert.AreEqual(1, sdkCalls);
            Assert.AreEqual("failed", (await catalog.ReadUserCommandAsync(commandId))!.State);
            Assert.HasCount(1, center.Query());
            Assert.AreEqual(conflict, center.Query().Single());
            Assert.AreEqual(conflict, (await catalog.ReadRemoteConflictProjectionsAsync()).Single());
            Assert.AreEqual("retained local content", await File.ReadAllTextAsync(localPath));
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
            if (side is { } preserved)
            {
                // A failed SDK call retains the already verified copy for the same choice.
                string copy = await new MirrorPulseConflictCopyStore(paths).PreserveAsync(conflict, preserved,
                    _ => ValueTask.FromException<Stream>(new AssertFailedException("Do not reopen a source after preservation committed.")));
                Assert.AreEqual(preserved == MirrorPulseConflictPreservedSide.Local ? "retained local content" : "retained remote content",
                    await File.ReadAllTextAsync(copy));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(MirrorPulseConflictAction.KeepLocal)]
    [DataRow(MirrorPulseConflictAction.KeepRemote)]
    [DataRow(MirrorPulseConflictAction.KeepBoth)]
    public async Task AFailedProductCompletionWriteCannotHideTheConflict(MirrorPulseConflictAction action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-completion", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
        await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "note.txt"), "retained local content");
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var conflict = Create("Docs/note.txt");
            await catalog.SaveRemoteConflictProjectionAsync(conflict);
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = "CREATE TRIGGER reject_remote_completion BEFORE UPDATE OF state ON user_commands WHEN NEW.state='resolved' BEGIN SELECT RAISE(ABORT,'Synthetic terminal receipt write failure'); END;";
                await trigger.ExecuteNonQueryAsync();
            }
            int sdkCalls = 0;
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) =>
            {
                sdkCalls++; return ValueTask.FromResult(CloudRemoteApplyEntryStatus.Applied);
            }, new MirrorPulseConflictCopyStore(paths), catalog, center, (_, _) =>
            {
                sdkCalls++; return ValueTask.FromResult(CloudRemoteConflictDismissalStatus.Dismissed);
            });
            Guid commandId = Guid.NewGuid();
            await Assert.ThrowsExactlyAsync<SqliteException>(() => actions.ApplyAsync(commandId, conflict, action,
                action == MirrorPulseConflictAction.KeepBoth ? MirrorPulseConflictPreservedSide.Local : null));
            Assert.AreEqual(1, sdkCalls);
            Assert.AreEqual("failed", (await catalog.ReadUserCommandAsync(commandId))!.State);
            Assert.HasCount(1, center.Query());
            Assert.AreEqual(conflict, center.Query().Single(), "The center must retain the product conflict until its observed completion is durable.");
            Assert.AreEqual(conflict, (await catalog.ReadRemoteConflictProjectionsAsync()).Single());
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(CloudRemoteApplyEntryStatus.Applied)]
    [DataRow(CloudRemoteApplyEntryStatus.AlreadyApplied)]
    public async Task AnActualSdkTerminalOutcomeCanResolveAProductCommand(CloudRemoteApplyEntryStatus status)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-observed-result", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var conflict = Create("Docs/note.txt");
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) => ValueTask.FromResult(status),
                new MirrorPulseConflictCopyStore(paths), catalog, center);
            Guid commandId = Guid.NewGuid();
            var result = await actions.ApplyAsync(commandId, conflict, MirrorPulseConflictAction.KeepRemote);
            Assert.IsTrue(result.Resolved); Assert.AreEqual(status, result.CfSharpStatus);
            Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId))!.State);
            Assert.IsEmpty(center.Query()); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnSdkFailureAndItsIndependentPersistenceFailureAreBothRetained(bool missingConflict)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-double-failure", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var conflict = Create("Docs/note.txt");
            var center = new MirrorPulseConflictCenter(); center.Upsert(conflict);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = "CREATE TRIGGER reject_remote_failure BEFORE UPDATE OF state ON user_commands WHEN NEW.state='failed' BEGIN SELECT RAISE(ABORT,'Synthetic failure-state persistence failure'); END;";
                await trigger.ExecuteNonQueryAsync();
            }
            Exception original = missingConflict ? new KeyNotFoundException("The SDK conflict is unavailable.") : new IOException("The SDK outcome is unavailable.");
            var actions = new MirrorPulseRemoteConflictActions((_, _, _) => ValueTask.FromException<CloudRemoteApplyEntryStatus>(original),
                new MirrorPulseConflictCopyStore(paths), catalog, center);
            Guid commandId = Guid.NewGuid();
            var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => actions.ApplyAsync(commandId, conflict, MirrorPulseConflictAction.KeepRemote));
            Assert.HasCount(2, error.InnerExceptions); Assert.AreSame(original, error.InnerExceptions[0]);
            Assert.IsInstanceOfType<SqliteException>(error.InnerExceptions[1]);
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId))!.State);
            Assert.AreEqual(conflict, center.Query().Single()); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
