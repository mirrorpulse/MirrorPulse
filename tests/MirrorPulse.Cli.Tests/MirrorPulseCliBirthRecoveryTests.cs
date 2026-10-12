using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliBirthRecoveryTests
{
    [TestMethod]
    [DataRow(false, 11)]
    [DataRow(true, 0)]
    public async Task CliInvokesTheTypedHostRecoveryAndKeepsUnverifiedWorkPending(bool verified, int expectedExit)
    {
        Guid actionId = Guid.NewGuid();
        string pipe = "MirrorPulse-birth-recovery-cli-test-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        int calls = 0;
        dispatcher.Register<NamespaceBirthRecoverArguments, MirrorPulseControlNamespaceBirthRecoveryResult>(MirrorPulseControlCommands.NamespaceBirthRecover,
            (arguments, _) =>
            {
                Assert.AreEqual(actionId, arguments.RecoveryId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceBirthRecoveryResult(actionId,
                    verified ? "Completed" : "RecoveryRequired", verified, true, verified, verified, verified));
            });
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            int exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "birth", "recover", actionId.ToString("D")],
                output, error, hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(expectedExit, exit); Assert.AreEqual(string.Empty, error.ToString());
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement receipt = document.RootElement.GetProperty("data");
            Assert.AreEqual(actionId, receipt.GetProperty("recoveryId").GetGuid());
            Assert.AreEqual(verified, receipt.GetProperty("verificationRecorded").GetBoolean());
            Assert.IsTrue(receipt.GetProperty("drained").GetBoolean());
            Assert.IsFalse(receipt.TryGetProperty("localObject", out _));
            Assert.IsFalse(receipt.TryGetProperty("dacl", out _));
            foreach (string invalid in new[] { "invalid", Guid.Empty.ToString("D"), actionId.ToString("D") + " extra" })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await MirrorPulseCliApplication.RunAsync(["--no-start", "namespace", "birth", "recover", .. invalid.Split(' ')],
                    invalidOutput, invalidError, hostOperations: operations, isPlatformSupported: () => true));
                Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(1, calls, "Invalid requests must not reach the Host recovery handler.");
        }
        finally { shutdown.Cancel(); await serve; }
    }
}
