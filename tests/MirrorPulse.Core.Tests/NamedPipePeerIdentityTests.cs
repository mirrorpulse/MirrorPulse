using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class NamedPipePeerIdentityTests
{
    [TestMethod]
    public async Task NativePeerIdentityRejectsWrongProcessOrWindowsSession()
    {
        string name = WorkerLaunchNonce.CreatePipeName();
        await using var server = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(name));
        Task accepted = server.WaitForConnectionAsync();
        await using NamedPipeClientStream client = await NamedPipeWorkerClient.ConnectAsync(name, TimeSpan.FromSeconds(2));
        await accepted;
        using Process current = Process.GetCurrentProcess();
        NamedPipePeerIdentity.ValidateClient(server, current.Id, current.SessionId);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => NamedPipePeerIdentity.ValidateClient(server, current.Id + 1, current.SessionId));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => NamedPipePeerIdentity.ValidateClient(server, current.Id, current.SessionId + 1));
        Assert.AreEqual(current.Id, NamedPipePeerIdentity.ValidateServer(client, current.Id));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => NamedPipePeerIdentity.ValidateServer(client, current.Id + 1));
        NamedPipePeerIdentity.ValidateServerImage(client, Environment.ProcessPath!);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => NamedPipePeerIdentity.ValidateServerImage(client,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe")));
    }

    [TestMethod]
    public async Task PreclaimedControlNameFailsWithSafeDiagnostic()
    {
        string name = $"MirrorPulse-claimed-{Guid.NewGuid():N}";
        await using var imposter = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(name) { MaxInstances = 18 });
        var legitimate = new MirrorPulseControlPipeServer((request, _) => ValueTask.FromResult(
            ControlResponseEnvelope.Success(request.RequestId)), name);
        MirrorPulseControlPipeClaimException error = await Assert.ThrowsExactlyAsync<MirrorPulseControlPipeClaimException>(() =>
            legitimate.ServeAsync(CancellationToken.None));
        Assert.IsFalse(error.Message.Contains(name, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ClientRejectsWrongServerBeforeSendingArguments()
    {
        string name = $"MirrorPulse-wrong-peer-{Guid.NewGuid():N}";
        int requests = 0;
        var server = new MirrorPulseControlPipeServer((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return ValueTask.FromResult(ControlResponseEnvelope.Success(request.RequestId));
        }, name);
        using var shutdown = new CancellationTokenSource();
        Task serving = server.ServeAsync(shutdown.Token);
        try
        {
            foreach (bool wrongImage in new[] { false, true })
            {
                var client = new MirrorPulseControlClient(new()
                {
                    PipeName = name,
                    ExpectedHostProcessId = wrongImage ? null : Environment.ProcessId + 1,
                    GetExpectedHostExecutablePath = wrongImage ? () => Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe") : null,
                });
                MirrorPulseControlException error = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(() =>
                    client.SendEnvelopeAsync(MirrorPulseControlCommands.InstanceCreate, new InstanceCreateArguments(
                        "id", "name", new Dictionary<string, string>(), new Dictionary<string, string>(), "secret-marker", false)));
                Assert.AreEqual(MirrorPulseControlErrorCodes.Unauthorized, error.Error.Code);
                Assert.IsFalse(error.ToString().Contains("secret-marker", StringComparison.Ordinal));
            }
            Assert.AreEqual(0, Volatile.Read(ref requests));
        }
        finally { shutdown.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [TestMethod]
    public async Task StaleProtocolSessionIsRejectedEvenForCorrectNativePeer()
    {
        InstanceId instance = InstanceId.New();
        WorkerSessionId session = WorkerSessionId.New();
        string name = WorkerLaunchNonce.CreatePipeName();
        await using var server = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(name));
        Task accepted = server.WaitForConnectionAsync();
        await using NamedPipeClientStream client = await NamedPipeWorkerClient.ConnectAsync(name, TimeSpan.FromSeconds(2));
        await accepted;
        var stale = new ControlFrameEnvelope(1, "Hello", Guid.NewGuid(), instance, WorkerSessionId.New(), false,
            JsonSerializer.SerializeToElement(new { }));
        await MirrorPulseControlPipeTransport.WriteFrameAsync(client, ControlFrameJsonCodec.Encode(stale));
        ControlFrameEnvelope received = ControlFrameJsonCodec.Decode(await LengthPrefixedFrameReader.ReadAsync(server));
        using Process current = Process.GetCurrentProcess();
        NamedPipePeerIdentity.ValidateClient(server, current.Id, current.SessionId);
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterInstanceProcessSupervisor.ValidateFrame(received, "Hello", instance, session));
        Assert.AreEqual(64, name["mirrorpulse-adapter-".Length..].Length);
        Assert.AreNotEqual(name, WorkerLaunchNonce.CreatePipeName());
    }
}
