using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliNamespaceImportTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImportUsesTypedPipePreparationExecutionAndRecoveryStatus(bool explicitId)
    {
        Guid parentId = Guid.NewGuid(), requestedId = Guid.NewGuid(); Guid retainedId = Guid.Empty;
        string sourcePath = Path.GetFullPath("source with spaces.bin");
        string digest = new('A', 64); DateTimeOffset prepared = DateTimeOffset.UtcNow;
        int calls = 0; bool completed = false;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceMemberPrepareImportArguments, MirrorPulseControlNamespaceImportPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareImport, (arguments, _) =>
            {
                Assert.AreEqual(parentId, arguments.ParentMemberId); Assert.AreEqual("import.bin", arguments.Name);
                Assert.AreEqual(sourcePath, arguments.SourcePath); Assert.AreNotEqual(Guid.Empty, arguments.OperationId);
                if (explicitId) Assert.AreEqual(requestedId, arguments.OperationId);
                retainedId = arguments.OperationId; calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceImportPreparation(retainedId, parentId, "import.bin", 42, digest, prepared));
            });
        dispatcher.Register<NamespaceMemberImportArguments, MirrorPulseControlNamespaceMemberCreation>(
            MirrorPulseControlCommands.NamespaceMemberImport, (arguments, _) =>
            {
                Assert.AreEqual(retainedId, arguments.OperationId); calls++; completed = true;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceMemberCreation(retainedId, retainedId, parentId, "Local/import.bin", false, true));
            });
        dispatcher.Register<NamespaceMemberImportStatusArguments, MirrorPulseControlNamespaceImportStatus>(
            MirrorPulseControlCommands.NamespaceMemberImportStatus, (arguments, _) =>
            {
                Assert.AreEqual(retainedId, arguments.OperationId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceImportStatus(retainedId, "Local/import.bin",
                    completed ? "Registered" : "RecoveryRequired", 42, digest, true, completed, completed, completed, completed, completed, !completed));
            });
        string pipe = "MirrorPulse-import-cli-test-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            string[] arguments = [parentId.ToString("D"), "import.bin", "source with spaces.bin"];
            if (explicitId) arguments = [.. arguments, "--operation-id", requestedId.ToString("D")];
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, "prepare-import", arguments, output, error));
            Assert.AreEqual(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
            Assert.AreEqual(retainedId, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual(42, data.GetProperty("length").GetInt64()); Assert.AreEqual(digest, data.GetProperty("sha256").GetString());
            Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _)); Assert.IsFalse(data.TryGetProperty("dacl", out _));
            using var pendingOutput = new StringWriter(); using var pendingError = new StringWriter();
            Assert.AreEqual(MirrorPulseControlExitCodes.Pending, await RunAsync(operations, "import-status", [retainedId.ToString("D")], pendingOutput, pendingError));
            using var pending = JsonDocument.Parse(pendingOutput.ToString());
            Assert.IsTrue(pending.RootElement.GetProperty("data").GetProperty("requiresRecovery").GetBoolean());
            using var executeOutput = new StringWriter(); using var executeError = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, "import", [retainedId.ToString("D")], executeOutput, executeError));
            using var statusOutput = new StringWriter(); using var statusError = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, "import-status", [retainedId.ToString("D")], statusOutput, statusError));
            foreach (string verb in new[] { "import", "import-status" })
                foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [retainedId.ToString("D"), "extra"] })
                {
                    using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                    Assert.AreEqual(2, await RunAsync(operations, verb, invalid, invalidOutput, invalidError));
                    Assert.AreEqual(string.Empty, invalidOutput.ToString());
                }
            foreach (string[] invalid in new string[][] { [], ["invalid", "import.bin", sourcePath],
                [parentId.ToString("D"), "../other", sourcePath], [parentId.ToString("D"), "..", sourcePath],
                [.. arguments, "--unknown"], [parentId.ToString("D"), "import.bin", sourcePath, "--operation-id", Guid.Empty.ToString("D")] })
            {
                using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, "prepare-import", invalid, invalidOutput, invalidError));
                Assert.AreEqual(string.Empty, invalidOutput.ToString());
            }
            Assert.AreEqual(4, calls, "Invalid input must not reach the Host pipe.");
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string verb, string[] arguments, TextWriter output, TextWriter error) =>
        MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "member", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
