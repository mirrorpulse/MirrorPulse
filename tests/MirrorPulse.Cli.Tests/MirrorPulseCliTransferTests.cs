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
    [DataRow("prepare")]
    [DataRow("move")]
    [DataRow("resume")]
    public async Task TransferCommandsKeepTheirOriginalIdentityAcrossTheTypedPipeAndRejectMalformedRequests(string verb)
    {
        bool prepare = verb == "prepare";
        bool placement = verb != "resume";
        string commandName = verb switch
        {
            "prepare" => MirrorPulseControlCommands.NamespaceTransferPrepare,
            "move" => MirrorPulseControlCommands.NamespaceTransferMove,
            _ => MirrorPulseControlCommands.NamespaceTransferResume
        };
        var value = prepare ? Status("Prepared") with { CopyStartedAt = null }
            : Status("Completed") with { RootsFenced = false, JournalAcknowledged = true, Completed = true };
        Guid parent = Guid.NewGuid(); int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        ValueTask<MirrorPulseControlTransferStatus> AssertPlacement(Guid operationId, Guid sourceMemberId, Guid targetParentMemberId, string targetName)
        {
            Assert.AreEqual(value.OperationId, operationId); Assert.AreEqual(value.SourceMemberId, sourceMemberId);
            Assert.AreEqual(parent, targetParentMemberId); Assert.AreEqual("moved.bin", targetName);
            calls++; return ValueTask.FromResult(value);
        }
        if (prepare)
            dispatcher.Register<NamespaceTransferPrepareArguments, MirrorPulseControlTransferStatus>(commandName, (arguments, _) =>
                AssertPlacement(arguments.OperationId, arguments.SourceMemberId, arguments.TargetParentMemberId, arguments.TargetName));
        else if (placement)
            dispatcher.Register<NamespaceTransferMoveArguments, MirrorPulseControlTransferStatus>(commandName, (arguments, _) =>
                AssertPlacement(arguments.OperationId, arguments.SourceMemberId, arguments.TargetParentMemberId, arguments.TargetName));
        else
            dispatcher.Register<NamespaceTransferResumeArguments, MirrorPulseControlTransferStatus>(commandName, (arguments, _) =>
            { Assert.AreEqual(value.OperationId, arguments.OperationId); calls++; return ValueTask.FromResult(value); });
        string pipe = "MirrorPulse-transfer-command-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ConnectTimeout = TimeSpan.FromSeconds(15) });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        string[] arguments = placement ? [value.OperationId.ToString("D"), value.SourceMemberId.ToString("D"), parent.ToString("D"), "moved.bin"] : [value.OperationId.ToString("D")];
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, true, verb, arguments, output, error), error.ToString());
            using var document = JsonDocument.Parse(output.ToString()); var status = document.RootElement.GetProperty("data");
            Assert.AreEqual(!prepare, status.GetProperty("completed").GetBoolean()); Assert.AreEqual(!prepare, status.GetProperty("journalAcknowledged").GetBoolean());
            Assert.AreEqual(prepare, status.GetProperty("rootsFenced").GetBoolean()); Assert.AreEqual(value.OperationId, status.GetProperty("operationId").GetGuid());
            Assert.AreEqual(value.Stage, status.GetProperty("stage").GetString());
            if (prepare)
                Assert.IsTrue(!status.TryGetProperty("copyStartedAt", out var copyStarted) || copyStarted.ValueKind == JsonValueKind.Null);
            List<string[]> invalid = [[], ["invalid"], [Guid.Empty.ToString("D")], [.. arguments, "extra"]];
            if (placement)
            {
                invalid.Add([arguments[0], "invalid", arguments[2], arguments[3]]);
                invalid.Add([arguments[0], arguments[1], Guid.Empty.ToString("D"), arguments[3]]);
                invalid.Add([arguments[0], arguments[1], arguments[2], "../other"]);
                invalid.Add([arguments[0], arguments[1], arguments[2], " "]);
            }
            foreach (var malformed in invalid)
            {
                using var rejectedOutput = new StringWriter(); using var rejectedError = new StringWriter();
                Assert.AreEqual(2, await RunAsync(operations, true, verb, malformed, rejectedOutput, rejectedError));
                Assert.AreEqual(string.Empty, rejectedOutput.ToString());
            }
            Assert.AreEqual(1, calls);
            Assert.IsTrue(MirrorPulseControlCommands.Catalog.Single(item => item.Name == commandName).Mutating);
        }
        finally { shutdown.Cancel(); await serve; }
    }

    [TestMethod]
    [DataRow("CopyMayHaveBegun")]
    [DataRow("TargetBirthPrepared")]
    [DataRow("SourceDeleteMayHaveBegun")]
    [DataRow("SourceNativeRetired")]
    [DataRow("SourceNameSettled")]
    public async Task TransferQueriesCarryBothRootScopesAndUnfinishedSettlementThroughTheVersionedPipe(string stage)
    {
        var value = Status(stage); int reads = 0, lists = 0;
        if (stage == "TargetBirthPrepared")
            value = value with { PendingTargetBinding = new(Guid.NewGuid(), value.PreparedAt.AddSeconds(2)) };
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceTransferStatusArguments, MirrorPulseControlTransferStatus>(MirrorPulseControlCommands.NamespaceTransferStatus, (arguments, _) =>
        { Assert.AreEqual(value.OperationId, arguments.OperationId); reads++; return ValueTask.FromResult(value); });
        dispatcher.Register<NamespaceTransfersArguments, MirrorPulseControlTransfers>(MirrorPulseControlCommands.NamespaceTransfers, (arguments, _) =>
        { Assert.IsTrue(arguments.RootId == value.SourceRootId || arguments.RootId == value.TargetRootId); lists++; return ValueTask.FromResult(new MirrorPulseControlTransfers([value])); });
        string pipe = "MirrorPulse-transfer-cli-" + Guid.NewGuid().ToString("N");
        using var shutdown = new CancellationTokenSource(); Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ConnectTimeout = TimeSpan.FromSeconds(15) });
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
                Assert.AreEqual(value.PendingTargetBinding is not null, status.TryGetProperty("pendingTargetBinding", out var pending));
                if (value.PendingTargetBinding is { } binding)
                {
                    Assert.AreEqual(binding.BirthOperationId, pending.GetProperty("birthOperationId").GetGuid());
                    Assert.AreEqual(binding.StartedAt, pending.GetProperty("startedAt").GetDateTimeOffset());
                }
                using var human = new StringWriter();
                Assert.AreEqual(0, await RunAsync(operations, false, query.Item1, [query.Item2.ToString("D")], human, error));
                StringAssert.Contains(human.ToString(), stage); StringAssert.Contains(human.ToString(), "completed: False");
                StringAssert.Contains(human.ToString(), "journal acknowledged: False");
                if (value.PendingTargetBinding is { } bindingStatus)
                    StringAssert.Contains(human.ToString(), $"target binding pending: {bindingStatus.BirthOperationId:D}");
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
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ConnectTimeout = TimeSpan.FromSeconds(15) });
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
