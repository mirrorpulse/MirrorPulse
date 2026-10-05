using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

InstanceId instance = InstanceId.Parse(args[1]);
WorkerSessionId session = WorkerSessionId.Parse(args[3]);
await using var pipe = new NamedPipeClientStream(".", args[5], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(10000);
Guid helloId = Guid.NewGuid();
string[] capabilities = ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];
await SendAsync(new(1, "Hello", helloId, instance, session, false, JsonSerializer.SerializeToElement(new
{
    supportedVersions = new { minimum = 1, maximum = 2 },
    capabilities,
})));
ControlFrameEnvelope ready = await ReadAsync();
if (ready.MessageType != "Ready" || !ready.IsResponse || ready.RequestId != helloId) return;
if (ready.ProtocolVersion == 2)
{
    JsonElement[] roots = ready.Payload.GetProperty("roots").EnumerateArray().ToArray();
    foreach (JsonElement root in roots)
    {
        string key = root.GetProperty("rootKey").GetString()!;
        if (root.GetProperty("configuration").GetProperty("sourcePath").GetString() != key + "-source")
            throw new InvalidDataException("Root configuration was misrouted.");
    }
}
await SendAsync(new(ready.ProtocolVersion, "Connected", Guid.NewGuid(), instance, session, false,
    JsonSerializer.SerializeToElement(new { })));
while (true)
{
    ControlFrameEnvelope request = await ReadAsync();
    if (request.MessageType == "Stop") return;
    if (request.MessageType == "Stat")
        await SendAsync(new(ready.ProtocolVersion, "StatResult", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = request.Payload.GetProperty("rootKey").GetString(), revision = "fixture-revision" })));
}

async Task<ControlFrameEnvelope> ReadAsync() => ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(pipe));
async Task SendAsync(ControlFrameEnvelope frame)
{
    byte[] payload = ControlFrameJsonCodec.Encode(frame);
    byte[] prefix = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
    await pipe.WriteAsync(prefix);
    await pipe.WriteAsync(payload);
    await pipe.FlushAsync();
}

namespace MirrorPulse.Worker.ProtocolFixture
{
    public static class ProtocolFixtureMarker;
}
