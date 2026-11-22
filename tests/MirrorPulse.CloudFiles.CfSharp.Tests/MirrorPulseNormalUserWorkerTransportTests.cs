using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNormalUserWorkerTransportTests
{
    private static readonly string[] ExpectedCalls = ["directory", "range-open", "range-read", "range-dispose", "stat", "upload", "create", "delete", "move"];

    [TestMethod]
    public async Task WorkerCallsAndLazyRangeBodiesNeverInheritNamespaceRole()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        var worker = new IdentityProbeWorker(session);
        var transport = CreateTransport(session, worker);
        var instance = InstanceId.New();
        var provider = new MirrorPulseDemandProvider([instance], transport, new MirrorPulseAdapterDirectoryPageSource(transport));
        byte[] identity = MirrorPulsePlaceholderIdentity.Create(instance, "remote-file", "revision").Encode();
        await session.RunNamespaceOperationAsync(async () =>
        {
            var page = await provider.FetchChildrenAsync("", identity, null);
            Assert.IsTrue(page.IsComplete);
            await using Stream range = await provider.OpenReadAsync("file.bin", identity, 4, 0, 4);
            byte[] body = new byte[4];
            await range.ReadExactlyAsync(body);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, body);
            Assert.AreEqual("revision", await transport.StatAsync(new(instance, "file.bin", "root"), default));
            using var upload = new MemoryStream([5, 6]);
            Assert.AreEqual("uploaded", await transport.UploadAsync(new(instance, "file.bin", "revision", upload, 2), default));
            Assert.AreEqual("created", await transport.CreateDirectoryAsync(new(instance, "root", "folder", Guid.NewGuid()), default));
            Assert.AreEqual("deleted", await transport.DeleteAsync(new(instance, "file.bin", "revision", false), default));
            Assert.AreEqual("moved", await transport.MoveAsync(new(instance, "file.bin", "renamed.bin", "revision", false), default));
            AssertRole(session, expected: true);
        });
        CollectionAssert.AreEquivalent(ExpectedCalls, worker.Calls.ToArray());
        AssertRole(session, expected: false);
    }

    [TestMethod]
    public async Task FailedOrCanceledSourceReadReturnsNoPartialBodyAndRestoresCaller()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        var worker = new IdentityProbeWorker(session);
        var transport = CreateTransport(session, worker);
        var request = new MirrorPulseWorkerReadRangeRequest(InstanceId.New(), "file.bin", default, 0, 5);
        await session.RunNamespaceOperationAsync(async () =>
        {
            await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => transport.ReadRangeAsync(request, default).AsTask());
            Assert.AreEqual(1, worker.Calls.Count(call => call == "range-dispose"));
            AssertRole(session, expected: true);
            worker.CancelDuringRead = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => transport.ReadRangeAsync(request with { Length = 4 }, default).AsTask());
            Assert.AreEqual(2, worker.Calls.Count(call => call == "range-dispose"));
            AssertRole(session, expected: true);
            int calls = worker.Calls.Count;
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => transport.ReadRangeAsync(
                request with { Length = AdapterWorkerReadRangeClient.MaximumRangeBytes + 1L }, default).AsTask());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => transport.ReadRangeAsync(request, canceled.Token).AsTask());
            Assert.HasCount(calls, worker.Calls.ToArray());
            worker.FailStat = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => transport.StatAsync(new(request.InstanceId, request.NormalizedPath), default).AsTask());
            AssertRole(session, expected: true);
        });
        AssertRole(session, expected: false);
    }

    [TestMethod]
    public async Task SessionDisposalDrainsLazySourceReadAndDisposal()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        var session = new MirrorPulseNamespaceExecutionSession();
        var worker = new IdentityProbeWorker(session) { PauseRead = true };
        var transport = CreateTransport(session, worker);
        Task<Stream> reading = transport.ReadRangeAsync(new(InstanceId.New(), "file.bin", default, 0, 4), default).AsTask();
        await worker.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposing = session.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposing.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => transport.StatAsync(new(InstanceId.New(), "file.bin"), default).AsTask());
        }
        finally { worker.ReleaseRead.TrySetResult(); }
        using Stream detached = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("range-dispose", worker.Calls.ToArray()[^1]);
        byte[] body = new byte[4];
        await detached.ReadExactlyAsync(body);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, body);
        await session.DisposeAsync();
    }

    private static MirrorPulseNormalUserWorkerTransport CreateTransport(MirrorPulseNamespaceExecutionSession session,
        IdentityProbeWorker worker) => new(session, worker, worker, worker, worker, worker);

    private static void AssertRole(MirrorPulseNamespaceExecutionSession session, bool expected)
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        Assert.AreEqual(session.OwnerSid, current.User);
        Assert.AreEqual(expected, new WindowsPrincipal(current).IsInRole(session.RoleSid));
    }

    internal sealed class IdentityProbeWorker(MirrorPulseNamespaceExecutionSession session) : IMirrorPulseWorkerRangeTransport,
        IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource,
        IMirrorPulseWorkerMutationTransport
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public MirrorPulseWorkerDirectoryPage? DirectoryPage { get; init; }
        public bool CancelDuringRead { get; set; }
        public bool PauseRead { get; init; }
        public bool FailStat { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(string call)
        {
            AssertRole(session, expected: false);
            Calls.Enqueue(call);
        }

        public async ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("range-open");
            return new LazyIdentityProbeStream(this);
        }

        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("upload");
            byte[] body = new byte[checked((int)request.Length)];
            await request.Content.ReadExactlyAsync(body, cancellationToken);
            CollectionAssert.AreEqual(new byte[] { 5, 6 }, body);
            return "uploaded";
        }

        public async ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("stat");
            if (FailStat) throw new IOException("Synthetic source failure.");
            return "revision";
        }

        public async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(
            MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("directory");
            return DirectoryPage ?? new([], default, true);
        }

        public async ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("create");
            return "created";
        }

        public async ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("delete");
            return "deleted";
        }

        public async ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Record("move");
            return "moved";
        }
    }

    private sealed class LazyIdentityProbeStream(IdentityProbeWorker worker) : MemoryStream([1, 2, 3, 4], writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            worker.Record("range-read");
            worker.ReadEntered.TrySetResult();
            if (worker.PauseRead) await worker.ReleaseRead.Task;
            if (worker.CancelDuringRead) throw new OperationCanceledException("Synthetic source cancellation.");
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            await Task.Yield();
            worker.Record("range-dispose");
            await base.DisposeAsync();
        }
    }
}
