using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliMemberRetirementTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetirementPreservesRecursiveIntentAndOperationIdsAcrossTheRealPipe(bool recursive)
    {
        Guid operationId = Guid.NewGuid(), memberId = Guid.NewGuid();
        int calls = 0, count = recursive ? 3 : 1;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceMemberPrepareRetirementArguments, MirrorPulseControlNamespaceMemberRetirementPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareRetirement, (arguments, _) =>
            {
                Assert.AreEqual(operationId, arguments.OperationId); Assert.AreEqual(memberId, arguments.MemberId);
                Assert.AreEqual(recursive, arguments.Recursive); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceMemberRetirementPreparation(operationId, memberId,
                    "Local/tree", true, DateTimeOffset.UtcNow, recursive, count));
            });
        var result = new MirrorPulseControlNamespaceMemberRetirementResult(operationId, memberId, "Local/tree", true,
            "Completed", true, true, recursive, count);
        dispatcher.Register<NamespaceMemberRetirementArguments, MirrorPulseControlNamespaceMemberRetirementResult>(
            MirrorPulseControlCommands.NamespaceMemberRetirement, (arguments, _) =>
            {
                Assert.AreEqual(operationId, arguments.OperationId); Assert.AreEqual(recursive, arguments.Recursive);
                calls++; return ValueTask.FromResult(result);
            });
        dispatcher.Register<NamespaceMemberRetirementStatusArguments, MirrorPulseControlNamespaceMemberRetirementResult>(
            MirrorPulseControlCommands.NamespaceMemberRetirementStatus, (arguments, _) =>
            {
                Assert.AreEqual(operationId, arguments.OperationId); Assert.AreEqual(recursive, arguments.Recursive);
                calls++; return ValueTask.FromResult(result);
            });
        string pipe = "MirrorPulse-retirement-cli-test-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            string[] flags = recursive ? ["--recursive"] : [];
            foreach (string verb in new[] { "prepare-retire", "retire", "retirement-status" })
            {
                string[] arguments = verb == "prepare-retire"
                    ? [memberId.ToString("D"), .. flags, "--operation-id", operationId.ToString("D")]
                    : [operationId.ToString("D"), .. flags];
                using var output = new StringWriter(); using var error = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, verb, arguments, output, error));
                Assert.AreEqual(string.Empty, error.ToString());
                using var json = JsonDocument.Parse(output.ToString());
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(operationId, data.GetProperty("operationId").GetGuid());
                Assert.AreEqual(recursive, data.GetProperty("recursive").GetBoolean());
                Assert.AreEqual(count, data.GetProperty("memberCount").GetInt32());
                foreach (string[] invalid in new string[][]
                {
                    [], ["invalid"], [Guid.Empty.ToString("D")], [.. arguments, "--recursive", "--recursive"],
                    [.. arguments, "--unknown"], [.. arguments, "--operation-id"],
                    [.. arguments, "--operation-id", operationId.ToString("D")]
                })
                {
                    using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                    Assert.AreEqual(2, await RunAsync(operations, verb, invalid, invalidOutput, invalidError));
                    Assert.AreEqual(string.Empty, invalidOutput.ToString());
                }
            }
            Assert.AreEqual(3, calls, "Malformed deletion requests must never reach the Host.");
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string verb, string[] arguments,
        TextWriter output, TextWriter error) => MirrorPulseCliApplication.RunAsync(
            ["--no-start", "--json", "namespace", "member", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
