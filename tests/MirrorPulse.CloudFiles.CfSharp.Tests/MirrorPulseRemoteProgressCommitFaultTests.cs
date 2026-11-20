using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteProgressCommitFaultTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalProgressTransactionRollsBackAfterItemProjectionCommits(bool caseOnly)
    {
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse-remote-progress-tests");
        string directory = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        Guid itemId = Guid.NewGuid(); string source = "Docs/original.txt";
        string target = caseOnly ? "Docs/ORIGINAL.txt" : "Docs/moved.txt";
        var batch = new CloudRemoteChangeBatch("original", ReadOnlyMemory<byte>.Empty,
            [new CloudRemoteChange("move", CloudRemoteChangeKind.Move, "remote", "v2", CloudItemKind.File, target, itemId, "v1", source)], new byte[] { 1 });
        var fault = new MirrorPulseRemoteProgressCommitFault(batch);
        CloudRemoteBatchState Progress(int count) => new(batch.BatchId, count == 0 ? batch.InitialCursor.Span : batch.FinalCursor.Span,
            count, 1, count == 0 ? CloudRemoteBatchStatus.Applying : CloudRemoteBatchStatus.Applied,
            new byte[] { 42 }, DateTimeOffset.UtcNow, batch.Fingerprint, count == 0 ? null : "move");
        try
        {
            await using (var store = await fault.Wrap(MirrorPulseCfSharpStateStoreFactory.Create(paths)).OpenAsync(new(paths.SyncRootPath)))
            {
                await using (var original = await store.BeginTransactionAsync())
                {
                    await original.Items.UpsertAsync(new(itemId, "remote", source, CloudItemKind.File, "v1", 1, false, DateTimeOffset.UtcNow));
                    await original.RemoteBatches.UpsertAsync(Progress(0)); await original.CommitAsync();
                }
                await using (var projection = await store.BeginTransactionAsync())
                {
                    await projection.Items.UpsertAsync(new(itemId, "remote", target, CloudItemKind.File, "v2", 1, false, DateTimeOffset.UtcNow));
                    await projection.RemoteBatches.UpsertAsync(new("unrelated", new byte[] { 7 }, 1, 1, CloudRemoteBatchStatus.Applied,
                        new byte[] { 41 }, DateTimeOffset.UtcNow));
                    await projection.CommitAsync(); Assert.AreEqual(0, fault.Failures);
                }
                await using (var progress = await store.BeginTransactionAsync())
                {
                    await progress.RemoteBatches.UpsertAsync(Progress(1));
                    await Assert.ThrowsExactlyAsync<IOException>(() => progress.CommitAsync().AsTask());
                }
                Assert.AreEqual(1, fault.Failures); Assert.AreEqual(1, fault.UncommittedProgress!.AppliedEntryCount);
            }
            await using var reopened = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
            await using var read = await reopened.BeginTransactionAsync();
            Assert.AreEqual(0, (await read.RemoteBatches.GetAsync(batch.BatchId))!.AppliedEntryCount);
            Assert.AreEqual(1, (await read.RemoteBatches.GetAsync("unrelated"))!.AppliedEntryCount);
            var item = await read.Items.GetByItemIdAsync(itemId);
            Assert.IsNotNull(item); Assert.AreEqual(target, item.RelativePath.Replace('\\', '/')); Assert.AreEqual("v2", item.RemoteRevision);
            CollectionAssert.AreEqual(batch.Fingerprint.ToArray(), (await read.RemoteBatches.GetAsync(batch.BatchId))!.Fingerprint.ToArray());
            await read.RollbackAsync();
        }
        finally { RemoveStateFixture(directory, fixtureRoot); }
    }

    private static void RemoveStateFixture(string directory, string fixtureRoot)
    {
        if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The state fixture escaped its temporary parent.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
