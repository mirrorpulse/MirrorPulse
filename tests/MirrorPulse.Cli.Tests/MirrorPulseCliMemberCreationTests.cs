using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliMemberCreationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CreationPreservesReusableOperationIdsAndRejectsInvalidInputBeforeThePipe(bool directory, bool explicitId)
    {
        Guid parentId = Guid.NewGuid(), operationId = Guid.NewGuid(); Guid? retainedId = null; int calls = 0;
        string name = directory ? "Folder" : "empty.bin";
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceMemberCreateArguments, MirrorPulseControlNamespaceMemberCreation>(MirrorPulseControlCommands.NamespaceMemberCreate,
            (arguments, _) =>
            {
                Assert.AreEqual(parentId, arguments.ParentMemberId); Assert.AreEqual(name, arguments.Name);
                Assert.AreEqual(directory, arguments.IsDirectory); Assert.AreNotEqual(Guid.Empty, arguments.OperationId);
                if (explicitId) Assert.AreEqual(operationId, arguments.OperationId);
                retainedId ??= arguments.OperationId; Assert.AreEqual(retainedId, arguments.OperationId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceMemberCreation(arguments.OperationId, arguments.OperationId,
                    parentId, "Local/" + name, directory, true));
            });
        string pipe = "MirrorPulse-member-creation-cli-test-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            string[] positional = [parentId.ToString("D"), name];
            string[] flags = directory ? ["--directory"] : [];
            string[] arguments = explicitId ? [.. positional, "--operation-id", operationId.ToString("D"), .. flags] : [.. positional, .. flags];
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, arguments, output, error)); Assert.AreEqual(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(retainedId, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual(parentId, data.GetProperty("parentMemberId").GetGuid());
            Assert.AreEqual(directory, data.GetProperty("isDirectory").GetBoolean()); Assert.IsTrue(data.GetProperty("registered").GetBoolean());
            Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _)); Assert.IsFalse(data.TryGetProperty("dacl", out _));
            using var replayOutput = new StringWriter(); using var replayError = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, [.. positional, .. flags, "--operation-id", retainedId!.Value.ToString("D")], replayOutput, replayError));
            Assert.AreEqual(output.ToString(), replayOutput.ToString());
            foreach (string[] invalid in new string[][]
            {
                [], ["invalid", name], [Guid.Empty.ToString("D"), name], [parentId.ToString("D"), "a/b"],
                [parentId.ToString("D"), "a\\b"], [parentId.ToString("D"), ".."], [.. positional, "extra"],
                [.. positional, "--directory", "--directory"], [.. positional, "--operation-id"],
                [.. positional, "--operation-id", Guid.Empty.ToString("D")], [.. positional, "--operation-id", "invalid"],
                [.. positional, "--operation-id", operationId.ToString("D"), "--operation-id", operationId.ToString("D")],
                [.. positional, "--unknown"]
            })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, invalid, invalidOutput, invalidError)); Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(2, calls, "Invalid creation requests must not reach the Host.");
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string[] arguments, TextWriter output, TextWriter error) =>
        MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "member", "create", .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
