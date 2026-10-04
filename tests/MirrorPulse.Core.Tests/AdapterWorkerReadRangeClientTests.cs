using System.IO.Pipes;
using System.Text;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterWorkerReadRangeClientTests
{
    [TestMethod]
    public async Task SdkWorkerRangeFrameHydratesTheRequestedBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-range-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instanceId = InstanceId.New();
        WorkerSessionId sessionId = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instanceId.Value, sessionId.Value);
        var host = new AdapterWorkerReadRangeClient(server, instanceId, sessionId);
        byte[] content = Encoding.UTF8.GetBytes("range-content");
        var request = new MirrorPulseWorkerReadRangeRequest(instanceId, "notes.txt",
            ReadOnlyMemory<byte>.Empty, 3, content.Length);

        Task<Stream> pending = host.ReadRangeAsync(request, timeout.Token).AsTask();
        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Assert.AreEqual("ReadRange", command.MessageType);
        Assert.AreEqual("notes.txt", command.Payload.GetProperty("path").GetString());
        Assert.AreEqual(3L, command.Payload.GetProperty("offset").GetInt64());
        Guid streamId = Guid.NewGuid();
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("ReadRangeReady", command.RequestId, true,
            new { streamId, length = content.Length }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        Task handling = host.HandleResponseAsync(response, timeout.Token).AsTask();
        await worker.SendChunkAsync(new AdapterBinaryChunk(command.RequestId, instanceId.Value,
            sessionId.Value, streamId, 3, content, true), timeout.Token);
        await handling;

        await using Stream result = await pending;
        byte[] received = new byte[content.Length];
        await result.ReadExactlyAsync(received);
        CollectionAssert.AreEqual(content, received);
        host.Close();
    }

    [TestMethod]
    public async Task CanceledConsumerDrainsItsDispatchedFrameBeforeTheNextRange()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var consumer = new CancellationTokenSource();
        string pipeName = $"mirrorpulse-range-cancel-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instance.Value, session.Value);
        var host = new AdapterWorkerReadRangeClient(server, instance, session);
        Task<Stream> canceled = host.ReadRangeAsync(new(instance, "note.txt", ReadOnlyMemory<byte>.Empty, 0, 1), consumer.Token).AsTask();
        AdapterControlFrame oldRequest = await worker.ReadAsync(timeout.Token);
        consumer.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
        Task<Stream> next = host.ReadRangeAsync(new(instance, "note.txt", ReadOnlyMemory<byte>.Empty, 1, 1), timeout.Token).AsTask();
        Guid oldStream = Guid.NewGuid();
        Task<byte[]> response = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("ReadRangeReady", oldRequest.RequestId, true, new { streamId = oldStream, length = 1 }, timeout.Token);
        ControlFrameEnvelope oldReady = ControlFrameJsonCodec.Decode(await response);
        Assert.IsTrue(host.CanHandle(oldReady), "A canceled consumer must not turn its late response into an uncorrelated session failure.");
        Assert.IsFalse(next.IsCompleted);
        Task handling = host.HandleResponseAsync(oldReady, timeout.Token).AsTask();
        await worker.SendChunkAsync(new(oldRequest.RequestId, instance.Value, session.Value, oldStream, 0, new byte[] { 10 }, true), timeout.Token);
        await handling;
        AdapterControlFrame newRequest = await worker.ReadAsync(timeout.Token);
        Guid newStream = Guid.NewGuid();
        response = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("ReadRangeReady", newRequest.RequestId, true, new { streamId = newStream, length = 1 }, timeout.Token);
        handling = host.HandleResponseAsync(ControlFrameJsonCodec.Decode(await response), timeout.Token).AsTask();
        await worker.SendChunkAsync(new(newRequest.RequestId, instance.Value, session.Value, newStream, 1, new byte[] { 20 }, true), timeout.Token);
        await handling;
        await using Stream result = await next;
        Assert.AreEqual(20, result.ReadByte());
        Assert.AreEqual(-1, result.ReadByte());
        host.Close();
    }

    [TestMethod]
    public async Task MismatchedBinaryOffsetFailsThePendingRange()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = $"mirrorpulse-range-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using AdapterNamedPipeClient workerPipe = await AdapterNamedPipeClient.ConnectAsync(
            pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instanceId = InstanceId.New();
        WorkerSessionId sessionId = WorkerSessionId.New();
        var worker = new AdapterControlChannel(workerPipe, instanceId.Value, sessionId.Value);
        var host = new AdapterWorkerReadRangeClient(server, instanceId, sessionId);
        var request = new MirrorPulseWorkerReadRangeRequest(instanceId, "notes.txt",
            ReadOnlyMemory<byte>.Empty, 3, 1);

        Task<Stream> pending = host.ReadRangeAsync(request, timeout.Token).AsTask();
        AdapterControlFrame command = await worker.ReadAsync(timeout.Token);
        Guid streamId = Guid.NewGuid();
        Task<byte[]> responseBytes = LengthPrefixedFrameReader.ReadAsync(server, timeout.Token).AsTask();
        await worker.SendAsync("ReadRangeReady", command.RequestId, true,
            new { streamId, length = 1 }, timeout.Token);
        ControlFrameEnvelope response = ControlFrameJsonCodec.Decode(await responseBytes);
        Task handling = host.HandleResponseAsync(response, timeout.Token).AsTask();
        await worker.SendChunkAsync(new AdapterBinaryChunk(command.RequestId, instanceId.Value,
            sessionId.Value, streamId, 4, new byte[] { 42 }, true), timeout.Token);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await handling);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await pending);
        host.Close();
    }
}
