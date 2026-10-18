using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRemoteRetirementTests
{
    private static readonly string[] JsonOptions = ["--json"];

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("conflict")]
    [DataRow("partial")]
    [DataRow("deleted")]
    public async Task RemoteDirectoryStatusPreservesUnknownDeletionConflictAndProgressThroughTheRealPipe(string mode)
    {
        var expected = Status(mode); int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteRetirementStatusArguments, MirrorPulseControlRemoteRetirementStatus>(
            MirrorPulseControlCommands.NamespaceRemoteRetirementStatus, (arguments, _) =>
            {
                Assert.AreEqual(expected.OperationId, arguments.OperationId); calls++; return ValueTask.FromResult(expected);
            });
        string pipe = "MirrorPulse-remote-status-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, true, "retirement-status", [expected.OperationId.ToString("D")], output, error));
            Assert.AreEqual(string.Empty, error.ToString());
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(expected.OperationId, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual(expected.Stage, data.GetProperty("stage").GetString());
            Assert.AreEqual(expected.SettledMembers, data.GetProperty("settledMembers").GetInt32());
            if (expected.RootNativeDeleted is { } deleted) Assert.AreEqual(deleted, data.GetProperty("rootNativeDeleted").GetBoolean());
            else Assert.IsTrue(!data.TryGetProperty("rootNativeDeleted", out var unknown) || unknown.ValueKind == JsonValueKind.Null);
            if (expected.ConflictId is { } conflict) Assert.AreEqual(conflict, data.GetProperty("conflictId").GetGuid());
            Assert.IsFalse(data.GetRawText().Contains("dacl", StringComparison.OrdinalIgnoreCase));
            using var human = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, false, "retirement-status", [expected.OperationId.ToString("D")], human, error));
            StringAssert.Contains(human.ToString(), "root deleted: " + (expected.RootNativeDeleted is { } native ? native ? "yes" : "no" : "unknown"));
            StringAssert.Contains(human.ToString(), expected.SettledMembers + "/65 settled");
            foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [expected.OperationId.ToString("D"), "extra"] })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, true, "retirement-status", invalid, invalidOutput, invalidError));
                Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(2, calls, "Invalid status requests must never reach the Host.");
        }
        finally { shutdown.Cancel(); await serve; }
    }

    [TestMethod]
    public async Task RemoteDirectoryListUsesBoundedRootPagingAndRejectsMalformedRequestsBeforeThePipe()
    {
        Guid root = Guid.NewGuid(), after = Guid.NewGuid(), next = Guid.NewGuid(); int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteRetirementsArguments, MirrorPulseControlRemoteRetirementPage>(
            MirrorPulseControlCommands.NamespaceRemoteRetirements, (arguments, _) =>
            {
                Assert.AreEqual(root, arguments.RootId); Assert.AreEqual(after, arguments.AfterOperationId); Assert.AreEqual(2, arguments.Limit);
                calls++; return ValueTask.FromResult(new MirrorPulseControlRemoteRetirementPage([Status("partial") with { RootId = root }], next));
            });
        string pipe = "MirrorPulse-remote-list-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] valid = [root.ToString("D"), "--after-operation-id", after.ToString("D"), "--limit", "2"];
            Assert.AreEqual(0, await RunAsync(operations, true, "retirements", valid, output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(next, data.GetProperty("nextOperationId").GetGuid()); Assert.AreEqual(1, data.GetProperty("entries").GetArrayLength());
            foreach (string[] invalid in new string[][]
            {
                [], ["invalid"], [Guid.Empty.ToString("D")], [root.ToString("D"), "--limit"],
                [root.ToString("D"), "--limit", "0"], [root.ToString("D"), "--limit", "65"],
                [root.ToString("D"), "--limit", "-1"], [.. valid, "--limit", "2"],
                [root.ToString("D"), "--after-operation-id", Guid.Empty.ToString("D")],
                [.. valid, "--after-operation-id", after.ToString("D")], [root.ToString("D"), "--unknown", "2"]
            })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, true, "retirements", invalid, invalidOutput, invalidError));
                Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(1, calls);
            foreach (string command in new[] { MirrorPulseControlCommands.NamespaceRemoteRetirementStatus, MirrorPulseControlCommands.NamespaceRemoteRetirements })
            {
                var metadata = MirrorPulseControlCommands.Catalog.Single(entry => entry.Name == command);
                Assert.IsFalse(metadata.Mutating); Assert.IsFalse(metadata.AcceptsSensitiveInput);
            }
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static MirrorPulseControlRemoteRetirementStatus Status(string mode)
    {
        bool complete = mode is "conflict" or "deleted"; bool? deleted = mode == "unknown" ? null : mode != "conflict";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "instance/original-directory", 0,
            "Docs/tree", complete ? "Completed" : "Prepared", complete ? "Completed" : mode == "partial" ? "SettlingMembers" : "InvocationMayHaveBegun",
            now.AddMinutes(-2), now.AddMinutes(-1), mode == "unknown" ? null : mode == "conflict" ? "Conflict" : "CommittedPrefix",
            deleted, deleted, mode == "unknown" ? null : mode == "conflict" ? "v1" : "v2", mode == "conflict" ? Guid.NewGuid() : null,
            mode == "conflict" ? "Delete" : null, 65, complete ? 65 : mode == "partial" ? 64 : 0,
            mode == "deleted" ? 65 : mode == "partial" ? 64 : mode == "conflict" ? 2 : 0, mode == "conflict" ? 63 : 0,
            complete ? -1 : mode == "partial" ? 0 : 64, mode == "unknown" ? null : now, complete ? now : null,
            "0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16");
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, bool json, string verb, string[] arguments,
        TextWriter output, TextWriter error) => MirrorPulseCliApplication.RunAsync(
            ["--no-start", .. (json ? JsonOptions : Array.Empty<string>()), "namespace", "remote", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
