using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliMemberPreparationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InspectionReturnsStableIdsThroughThePipeWithoutExposingPermissionEvidence(bool directory)
    {
        Guid memberId = Guid.NewGuid(), parentId = Guid.NewGuid();
        string relativePath = directory ? "Local/Folder" : "Local/file.bin";
        var dispatcher = new MirrorPulseControlDispatcher();
        int calls = 0;
        dispatcher.Register<NamespaceMemberInspectArguments, MirrorPulseControlNamespaceMemberInspection>(MirrorPulseControlCommands.NamespaceMemberInspect,
            (arguments, _) =>
            {
                Assert.AreEqual(relativePath, arguments.RelativePath); Assert.AreEqual(directory, arguments.IsDirectory); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceMemberInspection(memberId, parentId, "root", relativePath, directory, null, DateTimeOffset.UtcNow));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = directory ? [relativePath, "--directory"] : [relativePath];
            Assert.AreEqual(0, await RunAsync(operations, ["inspect", .. arguments], output, error));
            Assert.AreEqual(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(memberId, data.GetProperty("memberId").GetGuid());
            Assert.AreEqual(parentId, data.GetProperty("actualParentMemberId").GetGuid());
            Assert.AreEqual(directory, data.GetProperty("isDirectory").GetBoolean());
            Assert.IsFalse(data.TryGetProperty("dacl", out _)); Assert.IsFalse(data.TryGetProperty("localObject", out _));
            Assert.IsFalse(data.TryGetProperty("treeReady", out _));
            foreach (string[] invalid in new string[][] { [], [relativePath, "--unknown"], [relativePath, "--directory", "extra"] })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, ["inspect", .. invalid], invalidOutput, invalidError));
            }
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparationReturnsAReusableOperationIdAndRejectsInvalidRequestsBeforeDispatch(bool explicitId)
    {
        Guid operationId = Guid.NewGuid(), memberId = Guid.NewGuid(), parentId = Guid.NewGuid();
        Guid? retainedId = null;
        DateTimeOffset preparedAt = DateTimeOffset.UtcNow;
        var dispatcher = new MirrorPulseControlDispatcher();
        int calls = 0;
        dispatcher.Register<NamespaceMemberPrepareMoveArguments, MirrorPulseControlNamespaceMemberMovePreparation>(MirrorPulseControlCommands.NamespaceMemberPrepareMove,
            (arguments, _) =>
            {
                Assert.AreEqual(memberId, arguments.MemberId); Assert.AreEqual(parentId, arguments.TargetParentMemberId);
                Assert.AreEqual("renamed.bin", arguments.TargetName); Assert.AreNotEqual(Guid.Empty, arguments.OperationId);
                if (explicitId) Assert.AreEqual(operationId, arguments.OperationId);
                retainedId ??= arguments.OperationId; Assert.AreEqual(retainedId, arguments.OperationId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceMemberMovePreparation(arguments.OperationId, memberId,
                    parentId, "Local/file.bin", "Local/Folder/renamed.bin", preparedAt));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] positional = [memberId.ToString("D"), parentId.ToString("D"), "renamed.bin"];
            string[] arguments = explicitId ? [.. positional, "--operation-id", operationId.ToString("D")] : positional;
            Assert.AreEqual(0, await RunAsync(operations, ["prepare-move", .. arguments], output, error));
            Assert.AreEqual(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(retainedId, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual("Local/Folder/renamed.bin", data.GetProperty("targetRelativePath").GetString());
            Assert.IsFalse(data.TryGetProperty("committed", out _), "Preparation does not claim execution or source acceptance.");
            using var replayOutput = new StringWriter(); using var replayError = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, ["prepare-move", .. positional, "--operation-id", retainedId!.Value.ToString("D")], replayOutput, replayError));
            Assert.AreEqual(output.ToString(), replayOutput.ToString());
            foreach (string[] invalid in new string[][]
            {
                [], ["invalid", parentId.ToString("D"), "file"], [Guid.Empty.ToString("D"), parentId.ToString("D"), "file"],
                [memberId.ToString("D"), Guid.Empty.ToString("D"), "file"], [memberId.ToString("D"), parentId.ToString("D"), "a/b"],
                [memberId.ToString("D"), parentId.ToString("D"), "a\\b"], [.. positional, "extra"],
                [.. positional, "--operation-id", Guid.Empty.ToString("D")], [.. positional, "--operation-id", "invalid"],
                [.. positional, "--unknown", operationId.ToString("D")]
            })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, ["prepare-move", .. invalid], invalidOutput, invalidError));
                Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(2, calls, "Invalid requests must not create Host move admissions.");
        });
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string[] arguments, TextWriter output, TextWriter error) =>
        MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "member", .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);

    private static async Task WithPipeAsync(MirrorPulseControlDispatcher dispatcher, Func<MirrorPulseCliHostOperations, Task> test)
    {
        string pipe = "MirrorPulse-member-preparation-cli-test-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try { await test(operations); }
        finally { shutdown.Cancel(); await serve; }
    }
}
