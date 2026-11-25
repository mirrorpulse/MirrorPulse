using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliSyncRefreshTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SyncRefreshSendsTheRetryChoiceAndShowsTheRootStatus(bool force)
    {
        string pipe = "MirrorPulse-cli-sync-refresh-" + Guid.NewGuid().ToString("N");
        var instance = new MirrorPulseAppInstanceStatus(Guid.NewGuid().ToString("D"), "Documents", true, "Connected", null, null, null)
        {
            RemotePolling = [new(Guid.NewGuid().ToString("D"), "Retrying", "RootOffline", DateTimeOffset.UtcNow.AddSeconds(30), null)],
        };
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<SyncRefreshArguments, MirrorPulseAppStatusResponse>(MirrorPulseControlCommands.SyncRefresh, (arguments, _) =>
        {
            observed.TrySetResult(arguments.Force);
            return ValueTask.FromResult<MirrorPulseAppStatusResponse>(new(0, 0, [instance], []));
        });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            string[] arguments = force ? ["--no-start", "sync", "refresh", "--force"] : ["--no-start", "sync", "refresh"];
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = await MirrorPulseCliApplication.RunAsync(arguments, output, error, hostOperations: operations,
                isPlatformSupported: () => true, cancellationToken: shutdown.Token);
            Assert.AreEqual(0, exit, error.ToString());
            Assert.AreEqual(force, await observed.Task.WaitAsync(shutdown.Token));
            StringAssert.Contains(output.ToString(), "Remote scan: Retrying (RootOffline)");
            StringAssert.Contains(output.ToString(), "Next attempt:");
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
