using System.Buffers.Binary;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class HostWorkerIpcIntegrationTests
{
    [TestMethod]
    public async Task HostAndWorkerExchangeControlAndBinaryFramesOverNamedPipe()
    {
        var options = new NamedPipeServerOptions($"mirrorpulse-integration-{Guid.NewGuid():N}");
        await using var server = SecureNamedPipeServerFactory.Create(options);
        var serverConnection = server.WaitForConnectionAsync();
        await using var client = await NamedPipeWorkerClient.ConnectAsync(options.PipeName, TimeSpan.FromSeconds(2));
        await serverConnection;

        var instanceId = InstanceId.New();
        var sessionId = WorkerSessionId.New();
        using var payloadDocument = JsonDocument.Parse("{\"state\":\"ready\"}");
        var envelope = new ControlFrameEnvelope(1, "Ready", Guid.NewGuid(), instanceId, sessionId, true, payloadDocument.RootElement);
        await client.WriteAsync(CreateLengthPrefixedFrame(ControlFrameJsonCodec.Encode(envelope)));
        var decodedEnvelope = ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(server));

        Assert.AreEqual(envelope.MessageType, decodedEnvelope.MessageType);
        Assert.AreEqual(instanceId, decodedEnvelope.InstanceId);
        Assert.AreEqual("ready", decodedEnvelope.Payload.GetProperty("state").GetString());

        var chunk = new BinaryChunkFrame(Guid.NewGuid(), instanceId, sessionId, Guid.NewGuid(), 0, new byte[] { 8, 13, 21 }, true);
        await client.WriteAsync(CreateLengthPrefixedFrame(BinaryChunkCodec.Encode(chunk)));
        var decodedChunk = BinaryChunkCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(server));

        Assert.AreEqual(chunk.StreamId, decodedChunk.StreamId);
        Assert.IsTrue(chunk.Data.Span.SequenceEqual(decodedChunk.Data.Span));
        Assert.IsTrue(decodedChunk.EndOfStream);
    }

    private static byte[] CreateLengthPrefixedFrame(byte[] payload)
    {
        var frame = new byte[ControlFrameLimits.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(ControlFrameLimits.LengthPrefixBytes));
        return frame;
    }
}
