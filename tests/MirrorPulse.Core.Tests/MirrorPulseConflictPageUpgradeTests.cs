using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseConflictPageUpgradeTests
{
    [TestMethod]
    public async Task ExistingVersion92RecordsAcquireOnlyOptionalPageIndexesOnReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-page-upgrade-" + Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var detected = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8)).AddTicks(123);
        var upload = new MirrorPulseConflictRecord(Guid.NewGuid(), InstanceId.New(), "original-upload", "Docs/upload.txt",
            MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Diverged, "local-original", "remote-original", detected);
        var remote = new MirrorPulseConflictRecord(Guid.NewGuid(), InstanceId.New(), "original-remote", "Docs/remote.txt",
            MirrorPulseConflictReason.Delete, MirrorPulseVersionComparison.RemoteAdvanced, null, "remote-original", detected.AddTicks(1),
            source: MirrorPulseConflictSource.CfSharpRemote);
        string cleanupRoot = Path.GetFullPath(root);
        string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "MirrorPulse-conflict-page-upgrade-";
        if (!cleanupRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned upgrade fixture escaped its temporary parent.");
        try
        {
            await using (var first = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await first.SaveUploadConflictAsync(upload); await first.SaveRemoteConflictProjectionAsync(remote);
            }
            var options = new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
            await using (var original = new SqliteConnection(options.ToString()))
            {
                await original.OpenAsync(); await using var query = original.CreateCommand();
                query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(92L, await query.ExecuteScalarAsync());
                // Match the existing version92 catalog before optional page indexes.
                query.CommandText = "DROP INDEX ix_remote_conflicts_page; DROP INDEX ix_upload_conflicts_page;";
                await query.ExecuteNonQueryAsync();
                query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ix_remote_conflicts_page','ix_upload_conflicts_page');";
                Assert.AreEqual(0L, await query.ExecuteScalarAsync());
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                var page = await reopened.ReadPendingConflictPageAsync(1); Assert.AreEqual(remote, page.Items.Single());
                Assert.AreEqual(remote.DetectedAt.ToString("O"), page.Items[0].DetectedAt.ToString("O")); Assert.IsNotNull(page.NextCursor);
                var tail = await reopened.ReadPendingConflictPageAsync(1, page.NextCursor); Assert.AreEqual(upload, tail.Items.Single()); Assert.IsNull(tail.NextCursor);
                Assert.AreEqual(upload, await reopened.ReadUploadConflictAsync(upload.ConflictId));
                CollectionAssert.AreEqual(new[] { remote }, (await reopened.ReadRemoteConflictProjectionsAsync()).ToArray());
            }
            await using (var inspected = new SqliteConnection(options.ToString()))
            {
                await inspected.OpenAsync(); await using var query = inspected.CreateCommand();
                query.CommandText = "PRAGMA user_version;"; Assert.AreEqual(92L, await query.ExecuteScalarAsync());
                query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ix_remote_conflicts_page','ix_upload_conflicts_page');";
                Assert.AreEqual(2L, await query.ExecuteScalarAsync());
                query.CommandText = "SELECT (SELECT COUNT(*) FROM upload_conflicts)+(SELECT COUNT(*) FROM remote_conflict_projections);";
                Assert.AreEqual(2L, await query.ExecuteScalarAsync());
            }
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(paths.SyncRootPath));
        }
        finally
        {
            Directory.Delete(cleanupRoot, recursive: true);
        }
    }
}
