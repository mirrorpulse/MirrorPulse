using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlPipeAcceptRaceTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(5)]
    public async Task RealClientDisconnectBeforeAcceptCannotTerminateTheHostStartupOwner(int probes)
    {
        string name = $"MirrorPulse-control-accept-gap-{Guid.NewGuid():N}";
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, requests = 0;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return ValueTask.FromResult(ControlResponseEnvelope.Success(request.RequestId));
        }, name)
        {
            BeforeAccept = async token =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt <= probes)
                {
                    await using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await probe.ConnectAsync(2000, token);
                    // Dispose before production WaitForConnectionAsync: real Windows
                    // returns HRESULT800700E8 here; no mocked exception is injected.
                }
                else ready.TrySetResult();
            }
        };
        Task lifetime = MirrorPulseHostStartupLifetime.RunAsync(server.ServeAsync, async token =>
        {
            await ready.Task.WaitAsync(token); initialized.SetResult();
        }, shutdown.Token);
        try
        {
            Task first = await Task.WhenAny(initialized.Task, lifetime).WaitAsync(TimeSpan.FromSeconds(5));
            if (first == lifetime) await lifetime;
            Assert.IsTrue(initialized.Task.IsCompletedSuccessfully);
            Assert.IsFalse(lifetime.IsCompleted);
            foreach (string command in new[] { MirrorPulseControlCommands.HostStatus, MirrorPulseControlCommands.HostStop })
                Assert.IsTrue((await SendAsync(name, command, shutdown.Token)).Succeeded);
            Assert.AreEqual(2, requests); Assert.IsGreaterThan(probes, attempts);
        }
        finally
        {
            shutdown.Cancel(); await lifetime.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [TestMethod]
    public async Task PipeNameOwnershipSurvivesBothSidesOfTheDisconnectedAcceptHandover()
    {
        string name = $"MirrorPulse-control-accept-owner-{Guid.NewGuid():N}";
        int attempts = 0, refusals = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer((request, _) => ValueTask.FromResult(ControlResponseEnvelope.Success(request.RequestId)), name)
        {
            BeforeAccept = async token =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt > 2) return;
                if (attempt == 1)
                {
                    await using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await probe.ConnectAsync(2000, token);
                }
                var competitor = new MirrorPulseControlPipeServer((_, _) => throw new AssertFailedException("A competitor cannot own the channel."), name);
                await Assert.ThrowsExactlyAsync<MirrorPulseControlPipeClaimException>(() => competitor.ServeAsync(token));
                Interlocked.Increment(ref refusals);
                if (attempt == 2) ready.SetResult();
            }
        };
        Task serving = server.ServeAsync(shutdown.Token);
        try
        {
            Task first = await Task.WhenAny(ready.Task, serving).WaitAsync(TimeSpan.FromSeconds(5));
            if (first == serving) await serving;
            Assert.IsTrue((await SendAsync(name, MirrorPulseControlCommands.HostStatus, shutdown.Token)).Succeeded);
            Assert.AreEqual(2, refusals);
        }
        finally { shutdown.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [TestMethod]
    public async Task ShutdownDuringARealDisconnectedAcceptDrainsWithoutStartingAnotherListener()
    {
        string name = $"MirrorPulse-control-accept-cancel-{Guid.NewGuid():N}";
        int attempts = 0;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer((_, _) => throw new AssertFailedException("No request can run."), name)
        {
            BeforeAccept = async token =>
            {
                Interlocked.Increment(ref attempts);
                await using (var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                    await probe.ConnectAsync(2000, token);
                shutdown.Cancel();
            }
        };
        await server.ServeAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task UnrelatedAcceptSetupIoFailureStillStopsTheServingOwner()
    {
        var original = new IOException("Synthetic unrelated accept boundary failure.", unchecked((int)0x80070005));
        string name = $"MirrorPulse-control-accept-fatal-{Guid.NewGuid():N}";
        var server = new MirrorPulseControlPipeServer((_, _) => throw new AssertFailedException("No request can run."), name)
        { BeforeAccept = _ => Task.FromException(original) };
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => server.ServeAsync(CancellationToken.None));
        Assert.AreSame(original, failure);
    }

    private static async Task<ControlResponseEnvelope> SendAsync(string name, string command, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, token);
        using var arguments = JsonDocument.Parse("{}");
        var request = new ControlRequestEnvelope(1, Guid.NewGuid(), command, arguments.RootElement);
        await MirrorPulseControlPipeTransport.WriteFrameAsync(pipe, MirrorPulseControlJsonCodec.Serialize(request), token);
        return MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(await MirrorPulseControlPipeTransport.ReadFrameAsync(pipe, token));
    }
}
