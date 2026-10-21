using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliTransferTests
{
    private static readonly string[] JsonOptions = ["--json"];

    [TestMethod]
    [DataRow("CopyMayHaveBegun")]
    [DataRow("SourceDeleteMayHaveBegun")]
    [DataRow("SourceNativeRetired")]
    [DataRow("SourceNameSettled")]
    public async Task TransferQueriesCarryBothRootScopesAndUnfinishedSettlementThroughTheVersionedPipe(string stage)
    {
        var value = Status(stage); int reads = 0, lists = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceTransferStatusArguments, MirrorPulseControlTransferStatus>(MirrorPulseControlCommands.NamespaceTransferStatus, (arguments, _) =>
        { Assert.AreEqual(value.OperationId, arguments.OperationId); reads++; return ValueTask.FromResult(value); });
        dispatcher.Register<NamespaceTransfersArguments, MirrorPulseControlTransfers>(MirrorPulseControlCommands.NamespaceTransfers, (arguments, _) =>
        { Assert.IsTrue(arguments.RootId == value.SourceRootId || arguments.RootId == value.TargetRootId); lists++; return ValueTask.FromResult(new MirrorPulseControlTransfers([value])); });
        string pipe = "MirrorPulse-transfer-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            foreach (var query in new[] { ("status", value.OperationId), ("list", value.SourceRootId), ("list", value.TargetRootId) })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, true, query.Item1, [query.Item2.ToString("D")], output, error));
                using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
                var status = query.Item1 == "list" ? data.GetProperty("entries")[0] : data;
                Assert.AreEqual(value.OperationId, status.GetProperty("operationId").GetGuid()); Assert.AreEqual(stage, status.GetProperty("stage").GetString());
                Assert.AreEqual(value.SourceRootId, status.GetProperty("sourceRootId").GetGuid()); Assert.AreEqual(value.TargetRootId, status.GetProperty("targetRootId").GetGuid());
                Assert.IsTrue(status.GetProperty("rootsFenced").GetBoolean()); Assert.IsFalse(status.GetProperty("journalAcknowledged").GetBoolean());
                Assert.IsFalse(status.GetProperty("completed").GetBoolean());
                Assert.IsFalse(status.TryGetProperty("privateContentId", out _)); Assert.IsFalse(status.TryGetProperty("dacl", out _));
                using var human = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, false, query.Item1, [query.Item2.ToString("D")], human, error));
                StringAssert.Contains(human.ToString(), stage); StringAssert.Contains(human.ToString(), "completed: False");
                StringAssert.Contains(human.ToString(), "journal acknowledged: False");
                foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [query.Item2.ToString("D"), "extra"] })
                {
                    using var invalidOutput = new StringWriter(); using var invalidError = new StringWriter();
                    Assert.AreEqual(2, await RunAsync(operations, true, query.Item1, invalid, invalidOutput, invalidError));
                    Assert.AreEqual(string.Empty, invalidOutput.ToString());
                }
            }
            Assert.AreEqual(2, reads); Assert.AreEqual(4, lists);
            Assert.IsFalse(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceTransferStatus).Mutating);
            Assert.IsFalse(MirrorPulseControlCommands.Catalog.Single(item => item.Name == MirrorPulseControlCommands.NamespaceTransfers).Mutating);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    [TestMethod]
    public async Task TransferListReportsNoPendingWorkWithoutOpeningClientStorage()
    {
        Guid root = Guid.NewGuid(); int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceTransfersArguments, MirrorPulseControlTransfers>(MirrorPulseControlCommands.NamespaceTransfers, (arguments, _) =>
        { Assert.AreEqual(root, arguments.RootId); calls++; return ValueTask.FromResult(new MirrorPulseControlTransfers([])); });
        string pipe = "MirrorPulse-transfer-empty-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, false, "list", [root.ToString("D")], output, error));
            StringAssert.Contains(output.ToString(), "No pending transfers."); Assert.AreEqual(1, calls);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    private static MirrorPulseControlTransferStatus Status(string stage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Docs/original.txt", "Archive/moved.txt", stage, now.AddMinutes(-2), now.AddMinutes(-1), null, null, null, null,
            stage == "SourceNameSettled" ? now : null, RootsFenced: true, JournalAcknowledged: false, Completed: false);
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, bool json, string verb, string[] arguments,
        TextWriter output, TextWriter error) => MirrorPulseCliApplication.RunAsync(
            ["--no-start", .. (json ? JsonOptions : Array.Empty<string>()), "namespace", "transfer", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
