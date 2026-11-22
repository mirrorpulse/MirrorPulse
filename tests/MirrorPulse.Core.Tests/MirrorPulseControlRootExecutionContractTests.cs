using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlRootExecutionContractTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedRootExecutionKeepsOriginalOperationAndCurrentReadmissionAcrossTheUserPipe(bool ready)
    {
        var descriptor = MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.RootExecuteRename);
        Assert.IsTrue(descriptor.Mutating);
        Guid operation = Guid.NewGuid(); string root = Guid.NewGuid().ToString("D");
        var expected = new MirrorPulseControlRootExecution(operation, root, "Completed", "Docs", "Prior Label", "Current Label", 4, true, ready);
        string pipe = "MirrorPulse-root-execution-contract-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<RootExecuteRenameArguments, MirrorPulseControlRootExecution>(MirrorPulseControlCommands.RootExecuteRename,
            (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId); return ValueTask.FromResult(expected);
            });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        try
        {
            var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
            Assert.AreEqual(expected, await client.ExecuteRootRenameAsync(operation, shutdown.Token));
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
