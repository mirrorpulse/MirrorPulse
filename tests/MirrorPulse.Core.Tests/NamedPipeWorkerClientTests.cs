using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class NamedPipeWorkerClientTests
{
    [TestMethod]
    public async Task ClientConnectsToServerAndBothStreamsAreDuplex()
    {
        var options = new NamedPipeServerOptions($"mirrorpulse-client-{Guid.NewGuid():N}");
        await using var server = SecureNamedPipeServerFactory.Create(options);

        var serverConnection = server.WaitForConnectionAsync();
        await using var client = await NamedPipeWorkerClient.ConnectAsync(options.PipeName, TimeSpan.FromSeconds(2));
        await serverConnection;

        Assert.IsTrue(server.IsConnected);
        Assert.IsTrue(client.IsConnected);
        Assert.IsTrue(client.CanRead);
        Assert.IsTrue(client.CanWrite);
    }

    [TestMethod]
    public async Task ClientRejectsInvalidArguments()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => NamedPipeWorkerClient.ConnectAsync(" ", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => NamedPipeWorkerClient.ConnectAsync("mirrorpulse", TimeSpan.FromMilliseconds(-2)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => NamedPipeWorkerClient.ConnectAsync("mirrorpulse", TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1)));
    }
}
