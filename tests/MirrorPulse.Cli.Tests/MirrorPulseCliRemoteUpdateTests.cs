using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRemoteUpdateTests
{
    private static readonly string[] JsonOptions = ["--json"];

    [TestMethod]
    [DataRow("Prepared", null, null)]
    [DataRow("InvocationMayHaveBegun", null, null)]
    [DataRow("ProductSettled", "Applied", "SdkReturn")]
    [DataRow("ProductSettled", "Conflict", "RetainedSdkProgress")]
    public async Task OriginalUpdateStatusAndRecoveryUseTheVersionedPipeWithoutInventingAnOutcome(string stage, string? outcome, string? source)
    {
        var value = Status(stage, outcome, source); int reads = 0, recoveries = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteUpdateStatusArguments, MirrorPulseControlRemoteUpdateStatus>(
            MirrorPulseControlCommands.NamespaceRemoteUpdateStatus, (arguments, _) =>
            { Assert.AreEqual(value.OperationId, arguments.OperationId); reads++; return ValueTask.FromResult(value); });
        dispatcher.Register<NamespaceRemoteUpdateRecoverArguments, MirrorPulseControlRemoteUpdateStatus>(
            MirrorPulseControlCommands.NamespaceRemoteUpdateRecover, (arguments, _) =>
            { Assert.AreEqual(value.OperationId, arguments.OperationId); recoveries++; return ValueTask.FromResult(value); });
        string pipe = "MirrorPulse-original-update-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            foreach (string verb in new[] { "update-status", "recover-update" })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, true, verb, [value.OperationId.ToString("D")], output, error));
                using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
                Assert.AreEqual(value.OperationId, data.GetProperty("operationId").GetGuid()); Assert.AreEqual(stage, data.GetProperty("stage").GetString());
                foreach (var field in new[] { ("outcome", outcome), ("evidenceSource", source) })
                    if (field.Item2 is null) Assert.IsTrue(!data.TryGetProperty(field.Item1, out var property) || property.ValueKind == JsonValueKind.Null);
                    else Assert.AreEqual(field.Item2, data.GetProperty(field.Item1).GetString());
                Assert.IsFalse(data.TryGetProperty("cursorPublished", out _)); Assert.IsFalse(data.GetRawText().Contains("dacl", StringComparison.OrdinalIgnoreCase));
                using var human = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, false, verb, [value.OperationId.ToString("D")], human, error));
                StringAssert.Contains(human.ToString(), "product outcome: " + (outcome ?? "unknown"));
                StringAssert.Contains(human.ToString(), "evidence: " + (source ?? "unavailable"));
                foreach (string[] invalid in InvalidIds(value.OperationId))
                {
                    using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                    Assert.AreEqual(2, await RunAsync(operations, true, verb, invalid, invalidOutput, invalidError)); Assert.AreEqual(string.Empty, invalidOutput.ToString());
                }
            }
            Assert.AreEqual(2, reads); Assert.AreEqual(2, recoveries);
            Assert.IsFalse(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceRemoteUpdateStatus).Mutating);
            Assert.IsTrue(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceRemoteUpdateRecover).Mutating);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingUpdateQueryRetainsItsRootAndRejectsMalformedRequestsBeforeThePipe(bool pending)
    {
        Guid root = Guid.NewGuid(); int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteUpdatesArguments, MirrorPulseControlRemoteUpdates>(MirrorPulseControlCommands.NamespaceRemoteUpdates, (arguments, _) =>
        {
            Assert.AreEqual(root, arguments.RootId); calls++;
            return ValueTask.FromResult(new MirrorPulseControlRemoteUpdates(pending ? [Status("InvocationMayHaveBegun", null, null) with { RootId = root }] : []));
        });
        string pipe = "MirrorPulse-original-update-list-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, true, "updates", [root.ToString("D")], output, error));
            using var document = JsonDocument.Parse(output.ToString()); var entries = document.RootElement.GetProperty("data").GetProperty("entries");
            Assert.AreEqual(pending ? 1 : 0, entries.GetArrayLength());
            if (pending) Assert.AreEqual(root, entries[0].GetProperty("rootId").GetGuid());
            using var human = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, false, "updates", [root.ToString("D")], human, error));
            StringAssert.Contains(human.ToString(), pending ? "product outcome: unknown" : "No pending original remote updates.");
            foreach (string[] invalid in InvalidIds(root))
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, true, "updates", invalid, invalidOutput, invalidError));
            }
            Assert.AreEqual(2, calls);
            Assert.IsFalse(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceRemoteUpdates).Mutating);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static string[][] InvalidIds(Guid id) => [[], ["invalid"], [Guid.Empty.ToString("D")], [id.ToString("D"), "extra"]];

    private static MirrorPulseControlRemoteUpdateStatus Status(string stage, string? outcome, string? source)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Docs/versioned.txt", "original/batch", 1, "original-change", stage,
            now.AddMinutes(-2), stage == "Prepared" ? null : now.AddMinutes(-1), outcome, source, outcome is null ? null : now,
            "0.1.0-preview.7", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9");
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, bool json, string verb, string[] arguments,
        TextWriter output, TextWriter error) => MirrorPulseCliApplication.RunAsync(
            ["--no-start", .. (json ? JsonOptions : Array.Empty<string>()), "namespace", "remote", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
