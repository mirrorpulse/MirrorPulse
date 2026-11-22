using System.Text.Json;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlRootCancellationContractTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task TypedCancellationRetainsItsOriginalReceiptAndSeparatesCurrentAdmission(bool audited, bool ready)
    {
        Assert.IsTrue(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.RootCancelRename).Mutating);
        Guid operation = Guid.NewGuid();
        var expected = new MirrorPulseControlRootCancellation(operation, Guid.NewGuid().ToString("D"), "Cancelled", "Docs", "Prior Target",
            DateTimeOffset.UtcNow, "Current Label", 4, audited, ready);
        string pipe = "MirrorPulse-root-cancellation-contract-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<RootCancelRenameArguments, MirrorPulseControlRootCancellation>(MirrorPulseControlCommands.RootCancelRename,
            (arguments, _) => { Assert.AreEqual(operation, arguments.OperationId); return ValueTask.FromResult(expected); });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe); Task serving = server.ServeAsync(shutdown.Token);
        try
        {
            var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
            var actual = await client.CancelRootRenameAsync(operation, shutdown.Token);
            Assert.AreEqual(expected, actual);
            string json = JsonSerializer.Serialize(actual);
            Assert.DoesNotContain("DirectoryMoveEvidence", json); Assert.DoesNotContain("PlaceholderIdentity", json);
            Assert.DoesNotContain("OwnerSid", json); Assert.DoesNotContain("Dacl", json);
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
