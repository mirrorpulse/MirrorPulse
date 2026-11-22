using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliRemoteDeleteTests
{
    private static readonly string[] JsonOptions = ["--json"];
    [TestMethod]
    [DataRow("InvocationMayHaveBegun")]
    [DataRow("SettlingMembers")]
    [DataRow("CommandReceiptPending")]
    [DataRow("Completed")]
    public async Task ExplicitDeleteStatusAndRecoveryKeepTheirOriginalCommandAndUnknownOutcomeThroughTheRealPipe(string stage)
    {
        var value = Status(stage); int reads = 0, recoveries = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteDeleteStatusArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteStatus, (arguments, _) =>
            { Assert.AreEqual(value.CommandId, arguments.CommandId); reads++; return ValueTask.FromResult(value); });
        dispatcher.Register<NamespaceRemoteDeleteRecoverArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteRecover, (arguments, _) =>
            { Assert.AreEqual(value.CommandId, arguments.CommandId); recoveries++; return ValueTask.FromResult(value); });
        string pipe = "MirrorPulse-explicit-delete-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            foreach (string verb in new[] { "delete-status", "recover-delete" })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, true, verb, [value.CommandId.ToString("D")], output, error));
                using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
                Assert.AreEqual(value.CommandId, data.GetProperty("commandId").GetGuid()); Assert.AreEqual(value.Stage, data.GetProperty("stage").GetString());
                Assert.AreEqual(value.CommandCompleted, data.GetProperty("commandCompleted").GetBoolean());
                if (value.Outcome is null) Assert.IsTrue(!data.TryGetProperty("outcome", out var outcome) || outcome.ValueKind == JsonValueKind.Null);
                else Assert.AreEqual(value.Outcome, data.GetProperty("outcome").GetString());
                Assert.IsFalse(data.GetRawText().Contains("dacl", StringComparison.OrdinalIgnoreCase));
                using var human = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, false, verb, [value.CommandId.ToString("D")], human, error));
                StringAssert.Contains(human.ToString(), "SDK outcome: " + (value.Outcome ?? "unknown"));
                StringAssert.Contains(human.ToString(), "command complete: " + (value.CommandCompleted ? "yes" : "no"));
                foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [value.CommandId.ToString("D"), "extra"] })
                {
                    using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                    Assert.AreEqual(2, await RunAsync(operations, true, verb, invalid, invalidOutput, invalidError)); Assert.AreEqual(string.Empty, invalidOutput.ToString());
                }
            }
            Assert.AreEqual(2, reads); Assert.AreEqual(2, recoveries);
            Assert.IsFalse(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceRemoteDeleteStatus).Mutating);
            Assert.IsTrue(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceRemoteDeleteRecover).Mutating);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    [TestMethod]
    public async Task PendingDeletePagingRetainsItsRootAndBoundedContinuationWithoutMalformedPipeCalls()
    {
        Guid root = Guid.NewGuid(), after = Guid.NewGuid(), next = Guid.NewGuid(); int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceRemoteDeletesArguments, MirrorPulseControlRemoteDeletePage>(MirrorPulseControlCommands.NamespaceRemoteDeletes, (arguments, _) =>
        {
            Assert.AreEqual(root, arguments.RootId); Assert.AreEqual(after, arguments.AfterCommandId); Assert.AreEqual(2, arguments.Limit); calls++;
            return ValueTask.FromResult(new MirrorPulseControlRemoteDeletePage([Status("SettlingMembers") with { RootId = root }], next));
        });
        string pipe = "MirrorPulse-explicit-delete-page-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] valid = [root.ToString("D"), "--after-command-id", after.ToString("D"), "--limit", "2"];
            Assert.AreEqual(0, await RunAsync(operations, true, "deletes", valid, output, error));
            using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(next, data.GetProperty("nextCommandId").GetGuid()); Assert.AreEqual(1, data.GetProperty("entries").GetArrayLength());
            foreach (string[] invalid in new string[][] { [], ["invalid"], [root.ToString("D"), "--limit"], [root.ToString("D"), "--limit", "0"],
                [root.ToString("D"), "--limit", "65"], [root.ToString("D"), "--after-command-id", Guid.Empty.ToString("D")],
                [.. valid, "--limit", "2"], [.. valid, "--after-command-id", after.ToString("D")], [root.ToString("D"), "--unknown", "2"] })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, true, "deletes", invalid, invalidOutput, invalidError));
            }
            Assert.AreEqual(1, calls);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static MirrorPulseControlRemoteDeleteStatus Status(string stage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; bool members = stage is "CommandReceiptPending" or "Completed";
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Docs/retire", stage, 65,
            members ? 65 : stage == "SettlingMembers" ? 64 : 0, members ? -1 : stage == "SettlingMembers" ? 0 : 64,
            now.AddMinutes(-2), now.AddMinutes(-1), stage == "InvocationMayHaveBegun" ? null : "Reconciled",
            stage == "InvocationMayHaveBegun" ? null : now, members ? now : null, stage == "Completed",
            "0.1.0-preview.7", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9");
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, bool json, string verb, string[] arguments,
        TextWriter output, TextWriter error) => MirrorPulseCliApplication.RunAsync(
            ["--no-start", .. (json ? JsonOptions : Array.Empty<string>()), "namespace", "remote", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
