using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseTransferTargetReadTests
{
    private static readonly string[] Capabilities = ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];

    [TestMethod]
    public async Task PinnedTargetReadCannotSendAnyFrameOnAnActualLegacyChannel()
    {
        var instance = InstanceId.New(); using var pipe = new MemoryStream();
        var host = new AdapterWorkerReadRangeClient(pipe, instance, WorkerSessionId.New());
        var request = new MirrorPulseWorkerReadRangeRequest(instance, "copied.txt", ReadOnlyMemory<byte>.Empty, 0, 5, "target")
        { ExpectedRevision = "original-accepted-v1" };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => host.ReadRangeAsync(request).AsTask());
        Assert.AreEqual(0L, pipe.Length); host.Close();
    }

    [TestMethod]
    public async Task PinnedTargetReadSendsItsOriginalRevisionOnTheNegotiatedRootAndPath()
    {
        var instance = InstanceId.New(); using var pipe = new MemoryStream();
        var protocol = V2(instance);
        var host = new AdapterWorkerReadRangeClient(pipe, instance, WorkerSessionId.New(), protocol);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new MirrorPulseWorkerReadRangeRequest(instance, "copied.txt", ReadOnlyMemory<byte>.Empty, 3, 5, "target")
        { ExpectedRevision = "original-accepted-v1" };
        var pending = host.ReadRangeAsync(request, cancellation.Token).AsTask();
        try
        {
            using var encoded = new MemoryStream(pipe.ToArray());
            var frame = ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(encoded, cancellation.Token));
            Assert.AreEqual(2, frame.ProtocolVersion); Assert.AreEqual(instance, frame.InstanceId);
            Assert.AreEqual("ReadRange", frame.MessageType); Assert.AreEqual("target", frame.Payload.GetProperty("rootKey").GetString());
            Assert.AreEqual("copied.txt", frame.Payload.GetProperty("path").GetString());
            Assert.AreEqual("original-accepted-v1", frame.Payload.GetProperty("expectedRevision").GetString());
            Assert.AreEqual(3L, frame.Payload.GetProperty("offset").GetInt64()); Assert.AreEqual(5L, frame.Payload.GetProperty("length").GetInt64());
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending); host.Close();
    }

    private static AdapterWorkerProtocolSession V2(InstanceId instance)
    {
        var root = new RootRegistration(AdapterId.Parse("example.transfer"), instance, RootId.New(), "target", "Target", "Target",
            false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        return AdapterWorkerProtocolSession.Negotiate(JsonSerializer.SerializeToElement(new
        {
            supportedVersions = new { minimum = 2, maximum = 2 },
            capabilities = Capabilities
        }), [root], new(1, 2));
    }
}
