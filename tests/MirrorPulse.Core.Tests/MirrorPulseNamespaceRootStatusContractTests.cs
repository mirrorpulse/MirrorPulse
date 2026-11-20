using System.Text.Json;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseNamespaceRootStatusContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("legacy")]
    [DataRow("ready")]
    [DataRow("recovery")]
    public async Task RootAdmissionAndRecoveryRemainExplicitAcrossTheTypedCurrentUserPipe(string mode)
    {
        Guid operation = Guid.NewGuid(); var root = new MirrorPulseControlRoot("example.local", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), "docs", "Documents", "Documents", false, "Disabled", DateTimeOffset.UtcNow);
        var expected = new MirrorPulseControlRootStatus(root, mode == "recovery" ? "NamespaceRecovery" : "Disabled", false, null,
            mode == "legacy" ? null : mode == "ready", mode == "recovery" ? operation : null,
            mode == "recovery" ? "PendingNamespaceOperationRecoveryRequired" : null);
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, IReadOnlyList<MirrorPulseControlRootStatus>>(
            MirrorPulseControlCommands.RootList, (_, _) => ValueTask.FromResult<IReadOnlyList<MirrorPulseControlRootStatus>>([expected]));
        string pipe = "MirrorPulse-root-admission-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        try
        {
            var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
            Assert.AreEqual(expected, (await client.GetRootsAsync(shutdown.Token)).Single());
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(expected, JsonOptions));
            if (mode == "legacy") Assert.IsFalse(json.RootElement.TryGetProperty("namespaceReady", out _));
            else Assert.AreEqual(mode == "ready", json.RootElement.GetProperty("namespaceReady").GetBoolean());
            if (mode == "recovery") Assert.AreEqual(operation, json.RootElement.GetProperty("namespaceRecoveryOperationId").GetGuid());
        }
        finally { await shutdown.CancelAsync(); await serving; }
    }
}
