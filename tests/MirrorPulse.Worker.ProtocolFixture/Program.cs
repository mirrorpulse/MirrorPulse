using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Transport;

InstanceId instance = InstanceId.Parse(args[1]);
WorkerSessionId session = WorkerSessionId.Parse(args[3]);
string? cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR");
string? modePath = cache is null ? null : Path.Combine(cache, ".mp-startup-fixture-mode");
string mode = modePath is not null && File.Exists(modePath) ? (await File.ReadAllTextAsync(modePath)).Trim() : "Normal";
using FileStream? heldResource = mode is "IgnoreStopWithOpenFile" or "IdleBeforePipeWithOpenFile" or "NormalWithOpenFile"
    ? new FileStream(Path.Combine(cache!, ".mp-startup-fixture-open-resource"), FileMode.Create, FileAccess.ReadWrite, FileShare.None)
    : null;
if (heldResource is not null)
{
    string pid = Path.Combine(cache!, ".mp-startup-fixture-pid");
    await File.WriteAllTextAsync(pid + ".pending", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(pid + ".pending", pid);
}
if (mode == "ExitBeforePipe")
{
    while (!File.Exists(Path.Combine(cache!, ".mp-startup-fixture-exit"))) await Task.Delay(10);
    Environment.Exit(73);
}
if (mode is "IdleBeforePipe" or "IdleBeforePipeWithOpenFile") await Task.Delay(Timeout.InfiniteTimeSpan);
await using var pipe = new NamedPipeClientStream(".", args[5], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(10000);
if (mode == "IdleBeforeHello") await Task.Delay(Timeout.InfiniteTimeSpan);
if (mode == "UnexpectedHello")
{
    await SendAsync(new(1, "Connected", Guid.NewGuid(), instance, session, false,
        JsonSerializer.SerializeToElement(new { secret = "startup-secret-needle" })));
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
Guid helloId = Guid.NewGuid();
string[] capabilities = ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];
await SendAsync(new(1, "Hello", helloId, instance, session, false, JsonSerializer.SerializeToElement(new
{
    supportedVersions = new { minimum = mode == "RejectedHello" ? 2 : 1, maximum = 2 },
    capabilities,
})));
ControlFrameEnvelope ready = await ReadAsync();
if (mode == "RejectedHello") await Task.Delay(Timeout.InfiniteTimeSpan);
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
var files = new Dictionary<(string Root, string Path), (byte[] Content, string Revision)>
{
    [("left", "same.txt")] = (Encoding.UTF8.GetBytes("left"), "left/revision"),
    [("right", "same.txt")] = (Encoding.UTF8.GetBytes("rght"), "right/revision"),
};
var directories = new HashSet<(string Root, string Path)>();
var acceptedOperations = new Dictionary<Guid, (string Signature, string? Revision)>();
while (true)
{
    ControlFrameEnvelope request = await ReadAsync();
    if (request.MessageType == "Stop")
    {
        if (mode == "IgnoreStopWithOpenFile") await Task.Delay(Timeout.InfiniteTimeSpan);
        return;
    }
    string requestRoot = request.Payload.GetProperty("rootKey").GetString()!;
    string requestPath = request.Payload.TryGetProperty("path", out JsonElement pathElement) ? pathElement.GetString()! : "";
    if (request.MessageType == "Stat")
        await SendAsync(new(ready.ProtocolVersion, "StatResult", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = requestRoot, revision = files.TryGetValue((requestRoot, requestPath), out var file) ? file.Revision : directories.Contains((requestRoot, requestPath)) ? "directory" : null })));
    if (request.MessageType == "List")
        await SendAsync(new(ready.ProtocolVersion, "DirectoryPage", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = request.Payload.GetProperty("rootKey").GetString(), entries = Array.Empty<object>(), isComplete = true, cursor = (string?)null })));
    if (request.MessageType == "ReadRange")
    {
        string rootKey = request.Payload.GetProperty("rootKey").GetString()!;
        byte[] data = files[(rootKey, requestPath)].Content;
        Guid streamId = Guid.NewGuid();
        await SendAsync(new(ready.ProtocolVersion, "ReadRangeReady", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey, streamId, length = data.Length })));
        byte[] chunk = WorkerBinaryChunkV2Codec.Encode(new(request.RequestId, instance, session, streamId,
            request.Payload.GetProperty("offset").GetInt64(), data, true,
            Sha256Digest.Parse(Convert.ToHexString(SHA256.HashData(data))))
        { RootKey = rootKey });
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)chunk.Length));
        await pipe.WriteAsync(prefix);
        await pipe.WriteAsync(chunk);
        await pipe.FlushAsync();
    }
    if (request.MessageType is "CreateDirectory" or "Move" or "Delete" or "Upload")
    {
        Guid operationId = request.Payload.GetProperty("operationId").GetGuid();
        string destinationRoot = request.Payload.TryGetProperty("destinationRootKey", out JsonElement destination) ? destination.GetString()! : requestRoot;
        string destinationPath = request.Payload.TryGetProperty("destinationPath", out JsonElement target) ? target.GetString()! : requestPath;
        string signature = request.MessageType + "/" + requestRoot + "/" + requestPath + "/" + destinationRoot + "/" + destinationPath;
        string? revision;
        if (acceptedOperations.TryGetValue(operationId, out var accepted))
        {
            if (accepted.Signature != signature) throw new InvalidDataException("Operation ID changed binding.");
            revision = accepted.Revision;
        }
        else
        {
            if (request.MessageType == "Upload")
            {
                Guid streamId = request.Payload.GetProperty("streamId").GetGuid();
                long length = request.Payload.GetProperty("length").GetInt64();
                await SendAsync(new(2, "UploadReady", request.RequestId, instance, session, true,
                    JsonSerializer.SerializeToElement(new { rootKey = requestRoot, operationId, streamId })));
                using var data = new MemoryStream();
                while (true)
                {
                    BinaryChunkFrame chunk = WorkerBinaryChunkV2Codec.Decode(await LengthPrefixedFrameReader.ReadAsync(pipe));
                    if (chunk.RootKey != requestRoot || chunk.RequestId != request.RequestId || chunk.WorkerSessionId != session ||
                        chunk.InstanceId != instance || chunk.StreamId != streamId || chunk.Offset != data.Length || chunk.Data.Length > length - data.Length)
                        throw new InvalidDataException("Upload binding mismatch.");
                    data.Write(chunk.Data.Span);
                    if (!chunk.EndOfStream) continue;
                    if (data.Length != length) throw new InvalidDataException("Upload length mismatch.");
                    break;
                }
                byte[] content = data.ToArray();
                revision = Convert.ToHexString(SHA256.HashData(content));
                files[(requestRoot, requestPath)] = (content, revision);
            }
            else if (request.MessageType == "Move")
            {
                var source = files[(requestRoot, requestPath)];
                if (files.ContainsKey((destinationRoot, destinationPath))) throw new InvalidDataException("Destination exists.");
                files.Add((destinationRoot, destinationPath), source);
                files.Remove((requestRoot, requestPath));
                revision = source.Revision;
            }
            else if (request.MessageType == "Delete")
            {
                files.Remove((requestRoot, requestPath));
                directories.Remove((requestRoot, requestPath));
                revision = null;
            }
            else
            {
                if (!directories.Add((requestRoot, requestPath))) throw new InvalidDataException("Directory exists.");
                revision = "directory";
            }
            acceptedOperations.Add(operationId, (signature, revision));
        }
        await SendAsync(new(2, request.MessageType == "Upload" ? "UploadComplete" : "MutationComplete", request.RequestId, instance, session, true,
            JsonSerializer.SerializeToElement(new { rootKey = requestRoot, operationId, revision })));
    }
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
