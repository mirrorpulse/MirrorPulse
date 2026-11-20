using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRootAdmissionTests
{
    private static readonly string[] JsonArguments = ["--json", "--no-start", "root", "list"];
    private static readonly string[] HumanArguments = ["--no-start", "root", "list"];

    [TestMethod]
    [DataRow("legacy")]
    [DataRow("ready")]
    [DataRow("recovery")]
    public async Task RootListPreservesAdmissionAndOffersTheActualRecoveryIdentity(string mode)
    {
        Guid recovery = Guid.NewGuid(); var root = new MirrorPulseControlRoot("example.local", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), "docs", "Documents", "Documents", false, "Disabled", DateTimeOffset.UtcNow);
        var expected = new MirrorPulseControlRootStatus(root, mode == "recovery" ? "NamespaceRecovery" : "Disabled", false, null,
            mode == "legacy" ? null : mode == "ready", mode == "recovery" ? recovery : null,
            mode == "recovery" ? "PendingNamespaceOperationRecoveryRequired" : null);
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, IReadOnlyList<MirrorPulseControlRootStatus>>(
            MirrorPulseControlCommands.RootList, (_, _) => ValueTask.FromResult<IReadOnlyList<MirrorPulseControlRootStatus>>([expected]));
        string pipe = "MirrorPulse-root-admission-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        await using var operations = new MirrorPulseCliHostOperations(new(), new MirrorPulseControlClient(new() { PipeName = pipe }));
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await MirrorPulseCliApplication.RunAsync(JsonArguments, output, error, hostOperations: operations, isPlatformSupported: () => true));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data")[0];
            if (mode == "legacy") Assert.IsFalse(data.TryGetProperty("namespaceReady", out _));
            else Assert.AreEqual(mode == "ready", data.GetProperty("namespaceReady").GetBoolean());
            using var human = new StringWriter();
            Assert.AreEqual(0, await MirrorPulseCliApplication.RunAsync(HumanArguments, human, error, hostOperations: operations, isPlatformSupported: () => true));
            StringAssert.Contains(human.ToString(), root.RootId);
            if (mode == "recovery")
            {
                Assert.AreEqual(recovery, data.GetProperty("namespaceRecoveryOperationId").GetGuid());
                StringAssert.Contains(human.ToString(), recovery.ToString("D")); StringAssert.Contains(human.ToString(), expected.NamespaceRecoveryCode!);
            }
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
