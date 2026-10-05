using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class WorkerBinaryChunkV2Tests
{
    private static readonly string[] Capabilities = ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];
    [TestMethod]
    public void WrongRootResponseAndCorruptOrLegacyBinaryAreRejected()
    {
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        RootRegistration root = new(AdapterId.Parse("example.fixture"), instance, RootId.New(), "left", "Left", "Left", false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
        JsonElement offer = JsonSerializer.SerializeToElement(new { supportedVersions = new { minimum = 2, maximum = 2 }, capabilities = Capabilities });
        AdapterWorkerProtocolSession protocol = AdapterWorkerProtocolSession.Negotiate(offer, [root], new(1, 2));
        var response = new ControlFrameEnvelope(2, "StatResult", Guid.NewGuid(), instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = "right", revision = "revision" }));
        Assert.ThrowsExactly<InvalidDataException>(() => protocol.ValidateResponse(response, "left"));
        byte[] data = [1, 2, 3];
        var chunk = new BinaryChunkFrame(Guid.NewGuid(), instance, session, Guid.NewGuid(), 0, data, true,
            new(Convert.ToHexString(SHA256.HashData(data))))
        { RootKey = "left" };
        byte[] bytes = WorkerBinaryChunkV2Codec.Encode(chunk);
        Assert.AreEqual("left", WorkerBinaryChunkV2Codec.Decode(bytes).RootKey);
        bytes[^1] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => WorkerBinaryChunkV2Codec.Decode(bytes));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkerBinaryChunkV2Codec.Decode(BinaryChunkCodec.Encode(chunk)));
    }

    [TestMethod]
    public async Task WrongRootBinaryFailsAnActualPendingPipeRead()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pipeName = "mp-v2-negative-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await using var worker = await AdapterNamedPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), timeout.Token);
        await connection;
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        RootRegistration root = new(AdapterId.Parse("example.fixture"), instance, RootId.New(), "left", "Left", "Left", false, RootRegistrationState.Active, DateTimeOffset.UtcNow);
        var protocol = AdapterWorkerProtocolSession.Negotiate(JsonSerializer.SerializeToElement(new { supportedVersions = new { minimum = 2, maximum = 2 }, capabilities = Capabilities }), [root], new(1, 2));
        var host = new AdapterWorkerReadRangeClient(server, instance, session, protocol);
        Task<Stream> pending = host.ReadRangeAsync(new(instance, "same.txt", ReadOnlyMemory<byte>.Empty, 0, 1, "left"), timeout.Token).AsTask();
        ControlFrameEnvelope request = ControlFrameJsonCodec.Decode(await worker.ReadFrameAsync(timeout.Token));
        Guid streamId = Guid.NewGuid();
        var ready = new ControlFrameEnvelope(2, "ReadRangeReady", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = "left", streamId, length = 1 }));
        Task handling = host.HandleResponseAsync(ready, timeout.Token).AsTask();
        byte[] content = [1];
        await worker.WriteFrameAsync(WorkerBinaryChunkV2Codec.Encode(new(request.RequestId, instance, session, streamId, 0, content, true,
            new(Convert.ToHexString(SHA256.HashData(content))))
        { RootKey = "right" }), timeout.Token);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => handling);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pending);
        host.Close();
    }
}
