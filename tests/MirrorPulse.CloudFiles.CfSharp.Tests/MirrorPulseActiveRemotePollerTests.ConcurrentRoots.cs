using System.Collections.Concurrent;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseActiveRemotePollerTests
{
    [TestMethod]
    public async Task ManyConcurrentInstancesRotateTheirOwnRootsWithoutSharingProgress()
    {
        var adapter = AdapterId.Parse("example.parallel-roots");
        var roots = Enumerable.Range(0, 32).SelectMany(_ => AdapterRootRegistrationMapper.MapAll(adapter, InstanceId.New(),
            [new("first", "First", "First", false), new("second", "Second", "Second", false)], RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot)).ToArray();
        var instances = roots.GroupBy(root => root.InstanceId).Select(group => AdmissionAdapter(group.First())).ToArray();
        var source = new ConcurrentRootPollSource(); var applied = new ConcurrentQueue<(InstanceId Instance, CloudRemoteChangeBatch Batch)>();
        await using var poller = new MirrorPulseActiveRemotePoller(source, instances, roots, (instance, batch, _) =>
        {
            applied.Enqueue((instance, batch)); return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(true, batch.FinalCursor));
        });
        async Task TurnAsync(bool expected)
        {
            bool[] results = await Task.WhenAll(instances.Select(instance => poller.PollOnceAsync(instance.InstanceId).AsTask()));
            CollectionAssert.AreEqual(Enumerable.Repeat(expected, instances.Length).ToArray(), results);
        }
        await TurnAsync(false); await TurnAsync(false); source.Revision = "v2"; await TurnAsync(true); await TurnAsync(true);
        Assert.HasCount(64, applied);
        foreach (var instance in instances)
        {
            var own = applied.Where(value => value.Instance == instance.InstanceId).ToArray(); Assert.HasCount(2, own);
            Assert.HasCount(2, own.Select(value => MirrorPulseRemotePollBatchScope.Read(instance.InstanceId, value.Batch)).Distinct().ToArray());
            foreach (var root in roots.Where(root => root.InstanceId == instance.InstanceId))
                Assert.HasCount(2, source.Calls.Where(value => value.Instance == instance.InstanceId && value.RootKey == root.UniquenessKey).ToArray());
        }
    }

    private sealed class ConcurrentRootPollSource : IMirrorPulseDirectoryPageSource
    {
        internal string Revision { get; set; } = "v1";
        internal ConcurrentQueue<(InstanceId Instance, string RootKey)> Calls { get; } = new();
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instance, string path, ReadOnlyMemory<byte> cursor, int size, CancellationToken token) =>
            throw new InvalidOperationException("Each concurrent turn requires an explicit root.");
        public async ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instance, string rootKey, string path, ReadOnlyMemory<byte> cursor, int size, CancellationToken token)
        {
            Calls.Enqueue((instance, rootKey)); await Task.Yield(); token.ThrowIfCancellationRequested();
            return new([new("same", Revision, CloudItemKind.File, "file.bin", null, 1, CloudPlaceholderMetadata.CreateFileBuilder().Build(), false)],
                ReadOnlyMemory<byte>.Empty, true);
        }
    }
}
