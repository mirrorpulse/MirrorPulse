using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalUploadRevisionGuardTests
{
    [TestMethod]
    public async Task UsesCfSharpAcknowledgedRevisionAndRejectsChangedRemote()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-revision-guard", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            var factory = new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath);
            await using ICloudStateStore store = await factory.OpenAsync(
                new CloudStateStoreContext(paths.SyncRootPath));
            Guid itemId = Guid.NewGuid();
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await transaction.Items.UpsertAsync(new CloudItemState(itemId, "remote-note",
                    "WebDAV/note.txt", CloudItemKind.File, "\"base\"", null, false,
                    DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }

            var instanceId = InstanceId.New();
            var command = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, instanceId,
                "webdav", MirrorPulseWorkerChangeKind.ContentUpdate, "note.txt", null, null,
                false, itemId, DateTimeOffset.UtcNow);
            var stats = new FakeStats("\"base\"");
            string? expected = await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store,
                stats, command, "WebDAV/note.txt");
            Assert.AreEqual("\"base\"", expected);
            Assert.AreEqual("note.txt", stats.LastPath);
            Assert.AreEqual("\"base\"", await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(
                store, stats, command with { ItemId = null }, "WebDAV/note.txt"));

            stats.Revision = "\"remote-change\"";
            MirrorPulseUploadConflictException conflict = await Assert.ThrowsExactlyAsync<
                MirrorPulseUploadConflictException>(async () =>
                    await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats,
                        command, "WebDAV/note.txt"));
            Assert.AreEqual("\"base\"", conflict.ExpectedRevision);
            Assert.AreEqual("\"remote-change\"", conflict.ActualRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativePathLookupRetainsAcknowledgedRevisionWhenJournalIdentityIsUnavailable(bool staleItemId)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-revision-guard", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath)
                .OpenAsync(new CloudStateStoreContext(paths.SyncRootPath));
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await transaction.Items.UpsertAsync(new CloudItemState(Guid.NewGuid(), "remote-note",
                    "Local\\nested\\note.txt", CloudItemKind.File, "base", null, false, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }
            var command = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, InstanceId.New(), "local",
                MirrorPulseWorkerChangeKind.ContentUpdate, "nested\\note.txt", null, null, false,
                staleItemId ? Guid.NewGuid() : null, DateTimeOffset.UtcNow);
            var stats = new FakeStats("base");
            if (staleItemId)
            {
                MirrorPulseUploadConflictException unavailable = await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(async () =>
                    await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command, "Local/nested/note.txt"));
                Assert.IsNull(unavailable.ExpectedRevision);
                Assert.AreEqual("base", unavailable.ActualRevision);
                return;
            }
            Assert.AreEqual("base", await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command, "Local/nested/note.txt"));
            stats.Revision = "changed";
            MirrorPulseUploadConflictException conflict = await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(async () =>
                await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command, "Local/nested/note.txt"));
            Assert.AreEqual("base", conflict.ExpectedRevision);
            Assert.AreEqual("changed", conflict.ActualRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task EarlierProjectionInSameBatchUsesCurrentOfficialJournalReferenceWithoutLearningRemoteRevision()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-revision-guard", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        Guid originalItem = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        try
        {
            await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath)
                .OpenAsync(new CloudStateStoreContext(paths.SyncRootPath));
            long sequence;
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await transaction.Items.UpsertAsync(new CloudItemState(originalItem, "temporary", "Local\\note.txt",
                    CloudItemKind.File, null, null, false, DateTimeOffset.UtcNow));
                await transaction.Operations.EnqueueAsync(new CloudOperationJournalEntry(operation, CloudStateOperationKind.ContentUpdate,
                    originalItem, new byte[] { 1 }, DateTimeOffset.UtcNow));
                sequence = (await transaction.Operations.GetAsync(operation))!.Sequence;
                await transaction.CommitAsync();
            }
            var command = new MirrorPulseWorkerChangeCommand(operation, sequence, InstanceId.New(), "local",
                MirrorPulseWorkerChangeKind.ContentUpdate, "note.txt", null, null, false, originalItem, DateTimeOffset.UtcNow);
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await transaction.Items.RemoveAsync(originalItem);
                await transaction.Items.UpsertAsync(new CloudItemState(Guid.NewGuid(), "accepted-remote", "Local\\note.txt",
                    CloudItemKind.File, "accepted", null, false, DateTimeOffset.UtcNow));
                Assert.IsNull((await transaction.Operations.GetAsync(operation))!.ItemId);
                await transaction.CommitAsync();
            }
            var stats = new FakeStats("accepted");
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(async () =>
                await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command with { Sequence = sequence + 1 }, "Local/note.txt"));
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(async () =>
                await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command with { Kind = MirrorPulseWorkerChangeKind.Delete }, "Local/note.txt"));
            Assert.AreEqual("accepted", await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command, "Local/note.txt"));
            stats.Revision = "concurrent-change";
            MirrorPulseUploadConflictException conflict = await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(async () =>
                await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats, command, "Local/note.txt"));
            Assert.AreEqual("accepted", conflict.ExpectedRevision);
            Assert.AreEqual("concurrent-change", conflict.ActualRevision);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UntrackedCreateRequiresTheRemotePathToBeAbsent()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-revision-guard", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            var factory = new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath);
            await using ICloudStateStore store = await factory.OpenAsync(
                new CloudStateStoreContext(paths.SyncRootPath));
            var command = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, InstanceId.New(),
                "webdav", MirrorPulseWorkerChangeKind.Create, "new.txt", null, null,
                false, Guid.NewGuid(), DateTimeOffset.UtcNow);
            var stats = new FakeStats(null);
            Assert.IsNull(await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store,
                stats, command, "WebDAV/new.txt"));

            stats.Revision = "\"someone-else\"";
            await Assert.ThrowsExactlyAsync<MirrorPulseUploadConflictException>(async () =>
                await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(store, stats,
                    command, "WebDAV/new.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeStats(string? revision) : IMirrorPulseWorkerStatTransport
    {
        public string? Revision { get; set; } = revision;

        public string? LastPath { get; private set; }

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request,
            CancellationToken cancellationToken)
        {
            LastPath = request.NormalizedPath;
            return ValueTask.FromResult(Revision);
        }
    }
}
