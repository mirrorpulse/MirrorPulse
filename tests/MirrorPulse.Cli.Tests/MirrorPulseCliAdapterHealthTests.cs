using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliAdapterHealthTests
{
    [TestMethod]
    public async Task InstanceListExposesTheSameMaintenanceReasonToScriptsAndPeople()
    {
        string id = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-cli-health-" + Guid.NewGuid().ToString("N");
        var instance = new MirrorPulseControlInstance("example.adapter", "install", id, "Documents",
            new Dictionary<string, string>(), [], "files", "transfers", true, "Enabled", null, DateTimeOffset.UnixEpoch)
        {
            HealthIssues = [new(AdapterHealthReason.ExpiredCredential, "token", "Access token")],
        };
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<InstanceListArguments, MirrorPulseControlTopology>(MirrorPulseControlCommands.InstanceList,
            (_, _) => ValueTask.FromResult<MirrorPulseControlTopology>(new([], [instance], [], [])));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "instance", "list"], output, error,
                hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(0, exit);
            StringAssert.Contains(output.ToString(), $"Documents ({id}) · ExpiredCredential [token]");

            output.GetStringBuilder().Clear();
            exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "instance", "list"], output, error,
                hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(0, exit);
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            JsonElement issue = json.RootElement.GetProperty("data").GetProperty("instances")[0].GetProperty("healthIssues")[0];
            Assert.AreEqual("ExpiredCredential", issue.GetProperty("reason").GetString());
            Assert.AreEqual("token", issue.GetProperty("fieldKey").GetString());
            Assert.AreEqual("Access token", issue.GetProperty("fieldLabel").GetString());
            Assert.IsTrue(issue.GetProperty("requiresConfiguration").GetBoolean());
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
