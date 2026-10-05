using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlPipeConcurrencyTests
{
    [TestMethod]
    public async Task LongRequestDoesNotOwnStatusCancelOrStopAndOverloadIsExplicit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string name = $"MirrorPulse-control-concurrent-{Guid.NewGuid():N}";
        async ValueTask<ControlResponseEnvelope> Handle(ControlRequestEnvelope request, CancellationToken cancellationToken)
        {
            if (request.Command == MirrorPulseControlCommands.AdapterInstall)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return ControlResponseEnvelope.Success(request.RequestId);
        }

        var server = new MirrorPulseControlPipeServer(Handle, name, new()
        {
            MaximumConnections = 6,
            MaximumConcurrentRequests = 1,
            ReservedControlRequests = 2,
        });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = server.ServeAsync(shutdown.Token);
        Task<ControlResponseEnvelope> install = SendAsync(name, MirrorPulseControlCommands.AdapterInstall, shutdown.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (string command in new[] { MirrorPulseControlCommands.SyncStatus,
                MirrorPulseControlCommands.OperationCancel, MirrorPulseControlCommands.HostStop })
            {
                Assert.IsTrue((await SendAsync(name, command, shutdown.Token).WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            }
            ControlResponseEnvelope busy = await SendAsync(name, MirrorPulseControlCommands.AdapterList, shutdown.Token);
            Assert.AreEqual(MirrorPulseControlErrorCodes.HostBusy, busy.Error!.Code);
            Assert.IsTrue(busy.Error.Retryable);
            Assert.IsFalse(install.IsCompleted);
            release.TrySetResult();
            Assert.IsTrue((await install).Succeeded);
            Assert.IsTrue((await SendAsync(name, MirrorPulseControlCommands.AdapterList, shutdown.Token)).Succeeded);
        }
        finally
        {
            shutdown.Cancel();
            release.TrySetResult();
            await serving.WaitAsync(TimeSpan.FromSeconds(2));
            try { await install; } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ShutdownCancelsAndDrainsLongHandler()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string name = $"MirrorPulse-control-drain-{Guid.NewGuid():N}";
        async ValueTask<ControlResponseEnvelope> Handle(ControlRequestEnvelope request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { canceled.TrySetResult(); }
            return ControlResponseEnvelope.Success(request.RequestId);
        }
        var server = new MirrorPulseControlPipeServer(Handle, name);
        using var shutdown = new CancellationTokenSource();
        Task serving = server.ServeAsync(shutdown.Token);
        Task<ControlResponseEnvelope> request = SendAsync(name, MirrorPulseControlCommands.AdapterInstall, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        shutdown.Cancel();
        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(canceled.Task.IsCompleted);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () => await request);
    }

    private static async Task<ControlResponseEnvelope> SendAsync(string name, string command, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);
        using var arguments = JsonDocument.Parse("{}");
        var request = new ControlRequestEnvelope(1, Guid.NewGuid(), command, arguments.RootElement);
        await MirrorPulseControlPipeTransport.WriteFrameAsync(pipe, MirrorPulseControlJsonCodec.Serialize(request), cancellationToken);
        return MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(
            await MirrorPulseControlPipeTransport.ReadFrameAsync(pipe, cancellationToken));
    }
}
