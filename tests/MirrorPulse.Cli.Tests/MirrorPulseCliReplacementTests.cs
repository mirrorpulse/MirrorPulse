using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliReplacementTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparationPreservesExplicitIdsAndRejectsInvalidArgumentsBeforeThePipe(bool explicitId)
    {
        Guid member = Guid.NewGuid(), operation = Guid.NewGuid(); int calls = 0; Guid? received = null;
        string source = Path.GetFullPath("replacement-input.bin");
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceMemberPrepareReplaceArguments, MirrorPulseControlNamespaceReplacementPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareReplace, (arguments, _) =>
            {
                Assert.AreEqual(member, arguments.MemberId); Assert.AreEqual(source, arguments.SourcePath);
                Assert.AreNotEqual(Guid.Empty, arguments.OperationId); if (explicitId) Assert.AreEqual(operation, arguments.OperationId);
                received = arguments.OperationId; calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceReplacementPreparation(arguments.OperationId, member,
                    "Local/note.bin", 9, new string('A', 64), DateTimeOffset.UnixEpoch));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            string[] arguments = explicitId ? [member.ToString("D"), source, "--operation-id", operation.ToString("D")] : [member.ToString("D"), source];
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, "prepare-replace", arguments, output, error));
            using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(received, data.GetProperty("operationId").GetGuid()); Assert.AreEqual(member, data.GetProperty("memberId").GetGuid());
            Assert.IsFalse(data.TryGetProperty("sourcePath", out _)); Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _));
            foreach (string[] invalid in new string[][] { [], ["bad", source], [Guid.Empty.ToString("D"), source], [member.ToString("D")],
                [member.ToString("D"), source, "--operation-id"], [member.ToString("D"), source, "--operation-id", "bad"],
                [member.ToString("D"), source, "--unexpected", operation.ToString("D")] })
            {
                using var rejected = new StringWriter(); using var reason = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, "prepare-replace", invalid, rejected, reason));
            }
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    [DataRow("replace", false)]
    [DataRow("replace-recover", false)]
    [DataRow("replacement-status", true)]
    public async Task ExecuteRecoverAndHistoricalStatusUseTheVersionedHostContract(string verb, bool pending)
    {
        Guid operation = Guid.NewGuid(), member = Guid.NewGuid(); int executions = 0, queries = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceMemberReplaceArguments, MirrorPulseControlNamespaceReplacementCompletion>(
            MirrorPulseControlCommands.NamespaceMemberReplace, (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId); executions++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceReplacementCompletion(operation, member, "Local/note.bin", DateTimeOffset.UnixEpoch, true));
            });
        dispatcher.Register<NamespaceMemberReplacementStatusArguments, MirrorPulseControlNamespaceReplacementStatus>(
            MirrorPulseControlCommands.NamespaceMemberReplacementStatus, (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId); queries++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceReplacementStatus(operation, member, "Local/note.bin", "WriteStarted",
                    9, new string('A', 64), true, true, true, true, false, false, false, false, true));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(pending ? MirrorPulseControlExitCodes.Pending : 0,
                await RunAsync(operations, verb, [operation.ToString("D")], output, error));
            using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(operation, data.GetProperty("operationId").GetGuid());
            Assert.IsFalse(data.TryGetProperty("rescanGeneration", out _)); Assert.IsFalse(data.TryGetProperty("roleSid", out _));
            foreach (string[] invalid in new string[][] { [], ["bad"], [Guid.Empty.ToString("D")], [operation.ToString("D"), "extra"] })
            {
                using var rejected = new StringWriter(); using var reason = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, verb, invalid, rejected, reason));
            }
            Assert.AreEqual(pending ? 0 : 1, executions); Assert.AreEqual(pending ? 1 : 0, queries);
        });
    }

    private static async Task WithPipeAsync(MirrorPulseControlDispatcher dispatcher, Func<MirrorPulseCliHostOperations, Task> test)
    {
        string pipe = "MirrorPulse-replacement-cli-test-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try { await test(operations); }
        finally { shutdown.Cancel(); await serve; }
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string verb, string[] arguments, TextWriter output, TextWriter error) =>
        MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "member", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
