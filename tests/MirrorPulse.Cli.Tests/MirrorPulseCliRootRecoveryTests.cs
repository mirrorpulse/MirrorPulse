using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRootRecoveryTests
{
    [TestMethod]
    [DataRow("Prepared", 11)]
    [DataRow("NativeObserved", 11)]
    [DataRow("LocalProjected", 11)]
    [DataRow("Completed", 0)]
    [DataRow("Cancelled", 10)]
    public async Task CliReportsOriginalRecoveryPhaseWithoutClaimingPendingWorkCompleted(string phase, int expectedExit)
    {
        Guid operation = Guid.NewGuid();
        string rootId = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-root-recovery-cli-test-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        int calls = 0;
        dispatcher.Register<RootRecoverArguments, MirrorPulseControlRootRecovery>(MirrorPulseControlCommands.RootRecover,
            (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId);
                calls++;
                return ValueTask.FromResult(new MirrorPulseControlRootRecovery(operation, rootId, phase,
                    phase == "NativeObserved" ? true : null, phase == "NativeObserved" ? false : null,
                    phase == "NativeObserved" ? true : null, phase == "NativeObserved" ? "ProjectionIncomplete" : null,
                    phase == "NativeObserved" ? "Projection" : null, null));
            });
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            foreach (bool json in new[] { false, true })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                string[] arguments = json
                    ? ["--no-start", "--json", "root", "recover", operation.ToString("D")]
                    : ["--no-start", "root", "recover", operation.ToString("D")];
                int exit = await MirrorPulseCliApplication.RunAsync(arguments, output, error, hostOperations: operations, isPlatformSupported: () => true);
                Assert.AreEqual(expectedExit, exit);
                Assert.AreEqual(string.Empty, error.ToString());
                if (json)
                {
                    using JsonDocument document = JsonDocument.Parse(output.ToString());
                    JsonElement receipt = document.RootElement.GetProperty("data");
                    Assert.AreEqual(operation, receipt.GetProperty("operationId").GetGuid());
                    Assert.AreEqual(rootId, receipt.GetProperty("rootId").GetString());
                    Assert.AreEqual(phase, receipt.GetProperty("phase").GetString());
                    if (phase == "NativeObserved")
                    {
                        Assert.IsTrue(receipt.GetProperty("nativeMoveObserved").GetBoolean());
                        Assert.IsFalse(receipt.GetProperty("durableProjectionCommitted").GetBoolean());
                        Assert.IsTrue(receipt.GetProperty("requiresFullRescan").GetBoolean());
                    }
                    Assert.IsFalse(receipt.TryGetProperty("directoryMoveEvidence", out _));
                    Assert.IsFalse(receipt.TryGetProperty("placeholderIdentity", out _));
                }
                else
                {
                    StringAssert.Contains(output.ToString(), phase);
                    StringAssert.Contains(output.ToString(), operation.ToString("D"));
                    if (expectedExit == 11) Assert.IsFalse(output.ToString().Contains("Completed", StringComparison.Ordinal));
                }
            }
            Assert.AreEqual(2, calls);
            foreach (string invalid in new[] { "invalid", Guid.Empty.ToString("D"), operation.ToString("D") + " extra" })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "root", "recover", .. invalid.Split(' ')],
                    output, error, hostOperations: operations, isPlatformSupported: () => true);
                Assert.AreEqual(2, exit);
                Assert.AreEqual(string.Empty, output.ToString());
            }
            Assert.AreEqual(2, calls, "Invalid IDs or extra arguments must not reach the Host handler.");
        }
        finally { shutdown.Cancel(); await serve; }
    }
}
