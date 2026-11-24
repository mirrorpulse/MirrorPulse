using System.IO.Pipes;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterWorkerDirectoryPageClientTests
{
    [TestMethod]
    public async Task DirectoryPagePreservesOpaqueCursorAndEntries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-directory-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instanceId = InstanceId.New();
        WorkerSessionId sessionId = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instanceId.Value, sessionId.Value);
        var channel = new AdapterWorkerReadRangeClient(server, instanceId, sessionId);
        var pages = new AdapterWorkerDirectoryPageClient(channel, instanceId, sessionId);
        byte[] cursor = [1, 2, 3];
        Task<MirrorPulseWorkerDirectoryPage> pending = pages.ReadDirectoryPageAsync(
            new MirrorPulseWorkerDirectoryPageRequest(instanceId, string.Empty, cursor, 32),
            timeout.Token).AsTask();

        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Assert.AreEqual("List", command.MessageType);
        Assert.AreEqual(string.Empty, command.Payload.GetProperty("path").GetString());
        Assert.AreEqual(Convert.ToBase64String(cursor), command.Payload.GetProperty("cursor").GetString());
        Assert.AreEqual(32, command.Payload.GetProperty("pageSize").GetInt32());
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("DirectoryPage", command.RequestId, true, new
        {
            isComplete = false,
            cursor = Convert.ToBase64String(new byte[] { 4 }),
            entries = new[]
            {
                new
                {
                    remoteId = "remote-1",
                    remoteRevision = "revision-1",
                    itemKind = "File",
                    relativePath = "Folder/note.txt",
                    length = 9L,
                    creationTime = (DateTimeOffset?)null,
                    lastWriteTime = DateTimeOffset.UtcNow,
                    isDeleted = false,
                },
            },
        }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        await pages.HandleResponseAsync(response);

        MirrorPulseWorkerDirectoryPage page = await pending;
        Assert.IsFalse(page.IsComplete);
        CollectionAssert.AreEqual(new byte[] { 4 }, page.ContinuationCursor.ToArray());
        Assert.HasCount(1, page.Entries);
        Assert.AreEqual("Folder/note.txt", page.Entries[0].RelativePath);
        Assert.AreEqual(9L, page.Entries[0].Length);
        pages.Close();
        channel.Close();
    }

    [TestMethod]
    [DataRow("Offline", "Offline")]
    [DataRow("AccessDenied", "AccessDenied")]
    [DataRow("fixture-adapter-supplied-text", "Unknown")]
    public async Task DirectoryOperationErrorFailsPendingRequest(string workerCode, string expectedCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-directory-error-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instanceId = InstanceId.New();
        WorkerSessionId sessionId = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instanceId.Value, sessionId.Value);
        var channel = new AdapterWorkerReadRangeClient(server, instanceId, sessionId);
        var pages = new AdapterWorkerDirectoryPageClient(channel, instanceId, sessionId);
        Task<MirrorPulseWorkerDirectoryPage> pending = pages.ReadDirectoryPageAsync(
            new MirrorPulseWorkerDirectoryPageRequest(instanceId, "Folder", ReadOnlyMemory<byte>.Empty, 1),
            timeout.Token).AsTask();
        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("OperationError", command.RequestId, true, new { code = workerCode }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        await pages.HandleResponseAsync(response);
        var failure = await Assert.ThrowsExactlyAsync<AdapterWorkerOperationException>(async () => await pending);
        Assert.AreEqual(expectedCode, failure.FailureCode);
        Assert.AreEqual("The Adapter Worker operation failed.", failure.Message);
        pages.Close();
        channel.Close();
    }
}
