using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlPipeDeadlineTests
{
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("invalid-data")]
    [DataRow("argument")]
    [DataRow("io")]
    public async Task PartialResponseFailureCannotAppendAnotherFrameOrPoisonTheNextConnection(string fault)
    {
        Guid id = Guid.NewGuid();
        byte[] payload = MirrorPulseControlJsonCodec.Serialize(new ControlRequestEnvelope(MirrorPulseControlSchema.CurrentVersion,
            id, MirrorPulseControlCommands.SyncStatus, JsonSerializer.SerializeToElement(new ControlEmptyArguments())));
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length); payload.CopyTo(frame, 4);
        var server = new MirrorPulseControlPipeServer((request, _) => ValueTask.FromResult(
            ControlResponseEnvelope.Success(request.RequestId, JsonSerializer.SerializeToElement(new { value = 7 }))));
        using var ordinary = new SemaphoreSlim(1); using var control = new SemaphoreSlim(1); using var mutation = new SemaphoreSlim(1);
        using var failed = new PartialResponseStream(frame, fault);
        await server.HandleConnectionAsync(failed, ordinary, control, mutation, false, CancellationToken.None);
        // The peer saw a prefix, so another error frame would become bytes in its unfinished body.
        Assert.HasCount(4, failed.Output.ToArray(), "A failed response must close without appending a second frame.");
        using var healthy = new PartialResponseStream(frame, null);
        await server.HandleConnectionAsync(healthy, ordinary, control, mutation, false, CancellationToken.None);
        using var received = new MemoryStream(healthy.Output.ToArray());
        var response = MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(
            await MirrorPulseControlPipeTransport.ReadFrameAsync(received));
        Assert.AreEqual(id, response.RequestId); Assert.IsTrue(response.Succeeded);
        Assert.AreEqual(7, response.Data!.Value.GetProperty("value").GetInt32());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task IdlePartialAndOversizedPeerCannotBlockNextRequest(int mode)
    {
        // Measure the server frame boundary. Prepare the next frame before the
        // listener starts so first-use client serialization and peer inspection
        // cannot consume the deliberately short idle-peer read deadline.
        using var arguments = JsonDocument.Parse("{}");
        Guid requestId = Guid.NewGuid();
        byte[] payload = MirrorPulseControlJsonCodec.Serialize(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, requestId, MirrorPulseControlCommands.SyncStatus, arguments.RootElement));
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        string name = $"MirrorPulse-control-deadline-{Guid.NewGuid():N}";
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, MirrorPulseAppStatusResponse>(MirrorPulseControlCommands.SyncStatus,
            (_, _) => ValueTask.FromResult(new MirrorPulseAppStatusResponse(7, 0, [], [])));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, name, new()
        {
            FrameReadTimeout = TimeSpan.FromMilliseconds(150),
            FrameWriteTimeout = TimeSpan.FromMilliseconds(150),
            ConnectionTimeout = TimeSpan.FromSeconds(2),
        });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = server.ServeAsync(shutdown.Token);
        await using var stalled = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stalled.ConnectAsync(shutdown.Token);
            if (mode == 1) await stalled.WriteAsync(new byte[] { 1 }, shutdown.Token);
            if (mode >= 2)
            {
                byte[] prefix = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, mode == 2 ? 100U : uint.MaxValue);
                await stalled.WriteAsync(prefix, shutdown.Token);
            }

            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            await using var healthy = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await healthy.ConnectAsync(2000, requestDeadline.Token);
            await healthy.WriteAsync(frame, requestDeadline.Token);
            ControlResponseEnvelope response = MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(
                await MirrorPulseControlPipeTransport.ReadFrameAsync(healthy, requestDeadline.Token));
            Assert.AreEqual(requestId, response.RequestId);
            Assert.IsTrue(response.Succeeded);
            Assert.AreEqual(7, MirrorPulseControlJsonCodec.Deserialize<MirrorPulseAppStatusResponse>(
                JsonSerializer.SerializeToUtf8Bytes(response.Data!.Value)).PendingUploads);
            Assert.AreEqual(0, await stalled.ReadAsync(new byte[1], shutdown.Token));
        }
        finally
        {
            shutdown.Cancel();
            await serving.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public async Task ReaderRejectsSelectedLimitBeforeAllocatingOrReadingBody()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 1025);
        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LengthPrefixedFrameReader.ReadAsync(stream, maximumPayloadBytes: 1024).AsTask());
        Assert.AreEqual(4L, stream.Position);
    }

    [TestMethod]
    public async Task ShutdownCancelsConnectedIdlePeer()
    {
        string name = $"MirrorPulse-control-shutdown-{Guid.NewGuid():N}";
        var server = new MirrorPulseControlPipeServer((_, _) => throw new InvalidOperationException(), name);
        using var shutdown = new CancellationTokenSource();
        Task serving = server.ServeAsync(shutdown.Token);
        await using var peer = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await peer.ConnectAsync(2000);
        shutdown.Cancel();
        await serving.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class PartialResponseStream(byte[] input, string? fault) : Stream
    {
        private readonly MemoryStream _input = new(input);
        internal MemoryStream Output { get; } = new();
        private bool _failed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fault is not null && !_failed && buffer.Length > 4)
            {
                if (Output.Length == 0) Output.Write(buffer.Span[..4]);
                _failed = true;
                throw fault switch
                {
                    "cancel" => new OperationCanceledException("Injected after a visible response prefix."),
                    "invalid-data" => new InvalidDataException("Injected after a visible response prefix."),
                    "argument" => new ArgumentException("Injected after a visible response prefix."),
                    _ => new IOException("Injected after a visible response prefix.")
                };
            }
            Output.Write(buffer.Span); return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
