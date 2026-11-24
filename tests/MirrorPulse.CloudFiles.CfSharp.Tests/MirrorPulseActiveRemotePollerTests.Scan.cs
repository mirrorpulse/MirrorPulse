using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task OffsetShiftBetweenPagesDoesNotDeleteAnUnseenLiveFile()
    {
        CloudRemoteDirectoryEntry[] original = [ScanEntry("a"), ScanEntry("b"), ScanEntry("c")];
        var current = original;
        var shiftNextPage = false;
        var source = new ScanSource((_, cursor, size) =>
        {
            if (shiftNextPage && cursor.IsEmpty) return OffsetPage(original, cursor, size);
            if (shiftNextPage)
            {
                current = original[1..];
                shiftNextPage = false;
            }
            return OffsetPage(current, cursor, size);
        });
        var batches = new List<CloudRemoteChangeBatch>();
        var (poller, instanceId) = CreateScanPoller(source, batches, pageSize: 2);
        await using var lifecycle = poller;
        Assert.IsFalse(await poller.PollOnceAsync(instanceId));

        shiftNextPage = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => poller.PollOnceAsync(instanceId).AsTask());
        Assert.IsEmpty(batches);
        Assert.IsTrue(await poller.PollOnceAsync(instanceId));
        var change = batches.Single().Changes.Single();
        Assert.AreEqual(CloudRemoteChangeKind.Delete, change.Kind);
        Assert.AreEqual("Files\\a", change.RelativePath);
    }

    [TestMethod]
    public async Task TombstonesNeverBecomeLiveEntriesOrCauseRepeatedDeletes()
    {
        var entries = new[] { ScanEntry("file") };
        var source = new ScanSource((path, _, _) =>
        {
            Assert.AreEqual(string.Empty, path, "A tombstoned directory must never be opened.");
            return new(entries, ReadOnlyMemory<byte>.Empty, true);
        });
        var batches = new List<CloudRemoteChangeBatch>();
        var (poller, instanceId) = CreateScanPoller(source, batches, pageSize: 4);
        await using var lifecycle = poller;
        Assert.IsFalse(await poller.PollOnceAsync(instanceId));

        entries = [ScanEntry("file", deleted: true), ScanEntry("old-directory", CloudItemKind.Directory, deleted: true)];
        Assert.IsTrue(await poller.PollOnceAsync(instanceId));
        Assert.AreEqual(CloudRemoteChangeKind.Delete, batches.Single().Changes.Single().Kind);
        Assert.IsFalse(await poller.PollOnceAsync(instanceId));
        entries = [];
        Assert.IsFalse(await poller.PollOnceAsync(instanceId));
        Assert.HasCount(1, batches);
    }

    [TestMethod]
    public async Task ScansTenThousandEntriesAndRejectsContinuationCyclesWithinOneTurn()
    {
        var entries = Enumerable.Range(0, 10_000).Select(index => ScanEntry($"file-{index:D5}")).ToArray();
        var cycle = false;
        var reads = 0;
        var source = new ScanSource((_, cursor, size) =>
        {
            reads++;
            return cycle ? new([], new byte[] { 1 }, false) : OffsetPage(entries, cursor, size);
        });
        var batches = new List<CloudRemoteChangeBatch>();
        var (poller, instanceId) = CreateScanPoller(source, batches, pageSize: 128);
        await using var lifecycle = poller;
        Assert.IsFalse(await poller.PollOnceAsync(instanceId));
        Assert.AreEqual(158, reads);

        cycle = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => poller.PollOnceAsync(instanceId).AsTask());
        Assert.AreEqual(160, reads);
        Assert.IsEmpty(batches);

        cycle = false;
        entries = entries[1..^1];
        Assert.IsTrue(await poller.PollOnceAsync(instanceId));
        var changes = batches.Single().Changes;
        Assert.HasCount(2, changes);
        Assert.AreEqual("Files\\file-00000", changes[0].RelativePath);
        Assert.AreEqual("Files\\file-09999", changes[1].RelativePath);
        Assert.IsTrue(changes.All(change => change.Kind == CloudRemoteChangeKind.Delete));
    }

    private static (MirrorPulseActiveRemotePoller Poller, InstanceId InstanceId) CreateScanPoller(
        ScanSource source, List<CloudRemoteChangeBatch> batches, int pageSize)
    {
        InstanceId instance = InstanceId.New();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.scan"), instance,
            new AdapterRootDefinition("files", "Files", "Files", false), RootRegistrationState.Active);
        var adapter = new AdapterInstance(root.AdapterId, InstallId.New(), instance, "Files",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true,
            AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        return (new MirrorPulseActiveRemotePoller(source, [adapter], [root], (_, batch, _) =>
        {
            batches.Add(batch);
            return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        }, pageSize: pageSize), instance);
    }

    private static CloudRemoteDirectoryEntry ScanEntry(string path, CloudItemKind kind = CloudItemKind.File,
        bool deleted = false) => new(path, "r1", kind, path, null, kind == CloudItemKind.File ? 1L : null, null, deleted);

    private static CloudRemoteDirectoryPage OffsetPage(CloudRemoteDirectoryEntry[] entries, ReadOnlyMemory<byte> cursor, int size)
    {
        int offset = cursor.IsEmpty ? 0 : BitConverter.ToInt32(cursor.Span);
        int next = Math.Min(offset + size, entries.Length);
        bool complete = next == entries.Length;
        return new(entries[offset..next], complete ? ReadOnlyMemory<byte>.Empty : BitConverter.GetBytes(next), complete);
    }

    private sealed class ScanSource(Func<string, ReadOnlyMemory<byte>, int, CloudRemoteDirectoryPage> read) : IMirrorPulseDirectoryPageSource
    {
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken) =>
            ValueTask.FromResult(read(normalizedPath, continuationCursor, pageSize));
    }
}
