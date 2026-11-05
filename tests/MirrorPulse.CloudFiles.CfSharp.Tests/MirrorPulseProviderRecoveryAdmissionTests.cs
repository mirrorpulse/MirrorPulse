using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseProviderRecoveryAdmissionTests
{
    [TestMethod]
    [DataRow("file", "audited")]
    [DataRow("directory", "audited")]
    [DataRow("file", "cancelled")]
    [DataRow("directory", "cancelled")]
    [DataRow("file", "unaudited")]
    [DataRow("directory", "unaudited")]
    public async Task CallbackWaitsForItsExistingRecoveryWithoutReadingTheSource(string kind, string outcome)
    {
        var instance = InstanceId.New();
        var root = new RootRegistration(AdapterId.Parse("example.recovery-read"), instance, RootId.New(),
            "root", "Docs", "Docs", false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), "unused-provider-" + Guid.NewGuid().ToString("N")), [root]);
        var source = new Source();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await using (var initial = await work.BeginRecoveryAsync([root.RootId])) initial.CompleteAfterFreshAudit();
        var provider = new MirrorPulseDemandProvider(router, source, source, work);
        byte[] identity = router.CreateFileIdentity(instance, "root", "fixture-child").Encode();
        using var stop = new CancellationTokenSource();
        await using var recovery = await work.BeginRecoveryAsync([root.RootId]);
        Task<Stream>? file = kind == "file" ? provider.OpenReadAsync("Docs/child", identity, 1, 0, 1, stop.Token).AsTask() : null;
        Task<CloudProviderDirectoryPage>? directory = kind == "directory" ? provider.FetchChildrenAsync("Docs/child", identity, null, stop.Token).AsTask() : null;
        Task callback = file is null ? directory! : file;
        Assert.IsFalse(callback.IsCompleted, "The callback must await the existing recovery, without an ordinary lease or source read.");
        Assert.AreEqual(0, source.Calls);
        Assert.IsFalse(work.IsAdmitted(root.RootId));
        if (outcome == "cancelled")
        {
            stop.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => callback.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, source.Calls);
            return;
        }
        if (outcome == "audited") recovery.CompleteAfterFreshAudit();
        await recovery.DisposeAsync();
        if (outcome == "unaudited")
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => callback.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(work.IsAdmitted(root.RootId));
            Assert.AreEqual(0, source.Calls);
            return;
        }
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        if (file is not null)
        {
            await using var stream = await file;
            var next = work.BeginRecoveryAsync([root.RootId]).AsTask();
            Assert.IsFalse(next.IsCompleted, "The resumed lazy stream must retain its ordinary work lease.");
            byte[] bytes = new byte[1];
            Assert.AreEqual(1, await stream.ReadAsync(bytes));
            CollectionAssert.AreEqual(new byte[] { 42 }, bytes);
            await stream.DisposeAsync();
            await using var nextRecovery = await next.WaitAsync(TimeSpan.FromSeconds(5));
            nextRecovery.CompleteAfterFreshAudit();
        }
        else Assert.HasCount(0, (await directory!).Children);
        Assert.AreEqual(1, source.Calls);
    }

    private sealed class Source : IMirrorPulseWorkerRangeTransport, IMirrorPulseDirectoryPageSource
    {
        internal int Calls { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<Stream>(new MemoryStream([42], writable: false));
        }
        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(InstanceId instanceId, string normalizedPath,
            ReadOnlyMemory<byte> continuationCursor, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new CloudRemoteDirectoryPage([]));
        }
    }
}
