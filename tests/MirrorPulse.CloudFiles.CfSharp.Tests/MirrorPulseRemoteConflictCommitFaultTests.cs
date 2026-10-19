using CfSharp;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRemoteConflictCommitFaultTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostConflictRemovalRollsBackItsOwnCommitAfterTheOfficialTombstone(bool directory)
    {
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-explicit-delete-commit-tests");
        string directoryPath = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directoryPath, "sync"), Path.Combine(directoryPath, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        Guid itemId = Guid.NewGuid(), conflictId = Guid.NewGuid();
        var fault = new MirrorPulseRemoteConflictCommitFault();
        try
        {
            await using (var store = await fault.Wrap(MirrorPulseCfSharpStateStoreFactory.Create(paths)).OpenAsync(new(paths.SyncRootPath)))
            {
                await using (var original = await store.BeginTransactionAsync())
                {
                    await original.Items.UpsertAsync(new(itemId, "original", "Docs/retire", directory ? CloudItemKind.Directory : CloudItemKind.File,
                        "v1", directory ? null : 4, false, DateTimeOffset.UtcNow));
                    await original.Conflicts.UpsertAsync(new(conflictId, itemId, CloudStateConflictKind.Metadata, new byte[] { 42 }, DateTimeOffset.UtcNow));
                    await original.CommitAsync();
                }
                fault.Arm(conflictId, itemId, "v2");
                await using (var tombstone = await store.BeginTransactionAsync())
                {
                    await tombstone.Items.UpsertAsync(new(itemId, "original", "Docs/retire", directory ? CloudItemKind.Directory : CloudItemKind.File,
                        "v2", directory ? null : 4, true, DateTimeOffset.UtcNow));
                    await tombstone.CommitAsync(); Assert.AreEqual(0, fault.Failures);
                }
                await using (var removal = await store.BeginTransactionAsync())
                {
                    await removal.Conflicts.RemoveAsync(conflictId);
                    await Assert.ThrowsExactlyAsync<IOException>(() => removal.CommitAsync().AsTask());
                }
                Assert.AreEqual(1, fault.Failures);
            }
            await using var reopened = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
            await using (var read = await reopened.BeginTransactionAsync())
            {
                var item = (await read.Items.GetByItemIdAsync(itemId))!;
                Assert.IsTrue(item.IsTombstone); Assert.AreEqual("v2", item.RemoteRevision);
                Assert.IsNotNull(await read.Conflicts.GetAsync(conflictId));
                await read.RollbackAsync();
            }
            Assert.IsFalse(File.Exists(paths.ProductCatalogDatabasePath));
        }
        finally { RemoveFixture(directoryPath, fixtureRoot); }
    }

    private static void RemoveFixture(string directoryPath, string fixtureRoot)
    {
        if (!Path.GetFullPath(directoryPath).StartsWith(Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The official-store fixture escaped its own temporary parent.");
        if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, true);
    }
}
