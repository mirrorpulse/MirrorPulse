using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRootCancellationTests
{
    [TestMethod]
    [DataRow("Cancelled", true, 0)]
    [DataRow("Cancelled", false, 0)]
    [DataRow("Prepared", false, 11)]
    public async Task RootCancellationUsesTheOriginalTypedReceiptAndKeepsCurrentAdmissionSeparate(string phase, bool ready, int expectedExit)
    {
        Guid operation = Guid.NewGuid();
        string root = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-root-execution-cli-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        int calls = 0;
        dispatcher.Register<RootCancelRenameArguments, MirrorPulseControlRootCancellation>(MirrorPulseControlCommands.RootCancelRename,
            (request, _) =>
            {
                Assert.AreEqual(operation, request.OperationId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlRootCancellation(operation, root, phase,
                    "Documents", "Renamed", DateTimeOffset.UnixEpoch, "Current Label", 4, phase == "Cancelled", ready));
            });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            foreach (bool json in new[] { false, true })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                string[] arguments = json
                    ? ["--no-start", "--json", "root", "cancel-rename", operation.ToString("D")]
                    : ["--no-start", "root", "cancel-rename", operation.ToString("D")];
                int exit = await MirrorPulseCliApplication.RunAsync(arguments, output, error, hostOperations: operations, isPlatformSupported: () => true);
                Assert.AreEqual(expectedExit, exit); Assert.AreEqual(string.Empty, error.ToString());
                if (json)
                {
                    using var document = JsonDocument.Parse(output.ToString()); var receipt = document.RootElement.GetProperty("data");
                    Assert.AreEqual(operation, receipt.GetProperty("operationId").GetGuid());
                    Assert.AreEqual(root, receipt.GetProperty("rootId").GetString());
                    Assert.AreEqual(phase, receipt.GetProperty("phase").GetString());
                    Assert.AreEqual("Current Label", receipt.GetProperty("currentDirectoryName").GetString());
                    Assert.AreEqual(ready, receipt.GetProperty("namespaceReady").GetBoolean());
                    Assert.AreEqual(phase == "Cancelled", receipt.GetProperty("currentTreeAudited").GetBoolean());
                    Assert.IsFalse(receipt.TryGetProperty("directoryMoveEvidence", out _));
                    Assert.IsFalse(receipt.TryGetProperty("placeholderIdentity", out _));
                }
                else
                {
                    StringAssert.Contains(output.ToString(), phase); StringAssert.Contains(output.ToString(), "Current Label");
                }
            }
            Assert.AreEqual(2, calls);
            foreach (string[] invalid in new[] { Array.Empty<string>(), new[] { "invalid" }, new[] { Guid.Empty.ToString("D") }, new[] { operation.ToString("D"), "extra" } })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                int exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "root", "cancel-rename", .. invalid],
                    output, error, hostOperations: operations, isPlatformSupported: () => true);
                Assert.AreEqual(2, exit); Assert.AreEqual(string.Empty, output.ToString());
            }
            Assert.AreEqual(2, calls, "Malformed execution requests must not reach the Host or trigger startup.");
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
