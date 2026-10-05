using System.Buffers.Binary;
using System.IO.Pipes;
using MirrorPulse.Control.Client;
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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task IdlePartialAndOversizedPeerCannotBlockNextRequest(int mode)
    {
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

            var client = new MirrorPulseControlClient(new()
            {
                PipeName = name,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                RequestTimeout = TimeSpan.FromSeconds(3),
            });
            Assert.AreEqual(7, (await client.GetStatusAsync(cancellationToken: shutdown.Token)).PendingUploads);
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
}
