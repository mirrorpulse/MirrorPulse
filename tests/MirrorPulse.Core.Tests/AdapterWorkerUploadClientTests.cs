using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterWorkerUploadClientTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(14)]
    [DataRow(1048601)]
    public async Task UploadStreamsChunksAndReturnsWorkerRevision(int length)
    {
        Guid operationId = Guid.NewGuid();
        InstanceId instanceId = InstanceId.New();
        await UploadInNewSessionAsync(operationId, instanceId, length);
        await UploadInNewSessionAsync(operationId, instanceId, length);
    }

    private static async Task UploadInNewSessionAsync(Guid operationId, InstanceId instanceId, int length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-upload-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        WorkerSessionId sessionId = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instanceId.Value, sessionId.Value);
        var channel = new AdapterWorkerReadRangeClient(server, instanceId, sessionId);
        var upload = new AdapterWorkerUploadClient(channel, instanceId, sessionId);
        byte[] content = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new MemoryStream(content, writable: false);
        Task<string> pending = upload.UploadAsync(new MirrorPulseWorkerUploadRequest(
            instanceId, "notes.txt", "old-revision", source, content.Length, operationId,
            Convert.ToHexString(SHA256.HashData(content))), timeout.Token).AsTask();

        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Assert.AreEqual("Upload", command.MessageType);
        Assert.AreEqual(operationId, command.RequestId);
        Assert.AreEqual(operationId, command.Payload.GetProperty("operationId").GetGuid());
        Assert.AreEqual("old-revision", command.Payload.GetProperty("expectedRevision").GetString());
        Guid streamId = command.Payload.GetProperty("streamId").GetGuid();
        Assert.AreEqual(operationId, streamId);
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("UploadReady", command.RequestId, true, new { streamId }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        await upload.HandleResponseAsync(response);
        using var receivedContent = new MemoryStream();
        AdapterBinaryChunk received;
        do
        {
            received = await worker.ReadChunkAsync(timeout.Token);
            Assert.AreEqual(receivedContent.Length, received.Offset);
            await receivedContent.WriteAsync(received.Data, timeout.Token);
        } while (!received.EndOfStream);
        CollectionAssert.AreEqual(content, receivedContent.ToArray());

        Task<byte[]> completeBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("UploadComplete", command.RequestId, true,
            new { revision = "new-revision" }, timeout.Token);
        ControlFrameEnvelope complete = ControlFrameJsonCodec.Decode(await completeBytes);
        await upload.HandleResponseAsync(complete);
        Assert.AreEqual("new-revision", await pending);
        upload.Close();
        channel.Close();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(14)]
    [DataRow(1048601)]
    public async Task ChangedTransmittedContentCannotSendACommitFrame(int length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-upload-proof-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instance.Value, session.Value);
        var channel = new AdapterWorkerReadRangeClient(server, instance, session);
        var upload = new AdapterWorkerUploadClient(channel, instance, session);
        byte[] actual = new byte[length];
        byte[] expected = length == 0 ? [1] : new byte[length];
        expected[^1] = 1;
        await using var source = new MemoryStream(actual, writable: false);
        Task<string> pending = upload.UploadAsync(new(instance, "changed.bin", null, source, length,
            Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(expected))), timeout.Token).AsTask();
        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Guid streamId = command.Payload.GetProperty("streamId").GetGuid();
        Task<byte[]> response = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("UploadReady", command.RequestId, true, new { streamId }, timeout.Token);
        await upload.HandleResponseAsync(ControlFrameJsonCodec.Decode(await response));
        if (length > 1024 * 1024)
        {
            AdapterBinaryChunk partial = await worker.ReadChunkAsync(timeout.Token);
            Assert.IsFalse(partial.EndOfStream);
            Assert.AreEqual(1024 * 1024, partial.Data.Length);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await pending);
        using var noCommit = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await worker.ReadChunkAsync(noCommit.Token));
        upload.Close();
        channel.Close();
    }

    [TestMethod]
    public async Task StatReturnsNullableRevision()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-stat-test-{Guid.NewGuid():N}";
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
        var stat = new AdapterWorkerStatClient(channel, instanceId, sessionId);
        Task<string?> pending = stat.StatAsync(new MirrorPulseWorkerStatRequest(instanceId, "missing.txt"),
            timeout.Token).AsTask();
        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Assert.AreEqual("Stat", command.MessageType);
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("StatResult", command.RequestId, true, new { revision = (string?)null }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        await stat.HandleResponseAsync(response);
        Assert.IsNull(await pending);
        stat.Close();
        channel.Close();
    }

    [TestMethod]
    public async Task UploadConflictCarriesRevisionPreconditionsAcrossThePipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-upload-conflict-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        var worker = new AdapterControlChannel(pipe, instance.Value, session.Value);
        var channel = new AdapterWorkerReadRangeClient(server, instance, session);
        var client = new AdapterWorkerUploadClient(channel, instance, session);
        await using var bytes = new MemoryStream([1]);
        Task<string> pending = client.UploadAsync(new(instance, "file", "expected", bytes, 1, Guid.NewGuid()), timeout.Token).AsTask();
        AdapterControlFrame request = await worker.ReadAsync(timeout.Token);
        Task<byte[]> response = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("OperationError", request.RequestId, true,
            new { code = "RemoteConflict", expectedRevision = "expected", actualRevision = "changed" }, timeout.Token);
        await client.HandleResponseAsync(ControlFrameJsonCodec.Decode(await response));
        MirrorPulseWorkerMutationConflictException conflict = await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(async () => await pending);
        Assert.AreEqual("expected", conflict.ExpectedRevision);
        Assert.AreEqual("changed", conflict.ActualRevision);
        client.Close();
        channel.Close();
    }
}
