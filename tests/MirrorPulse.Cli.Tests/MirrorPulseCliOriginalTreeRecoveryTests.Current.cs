using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;

namespace MirrorPulse.Cli.Tests;

public sealed partial class MirrorPulseCliOriginalTreeRecoveryTests
{
    [TestMethod]
    [DataRow(false, "Completed")]
    [DataRow(true, "Completed")]
    [DataRow(false, "Pending")]
    [DataRow(true, "Pending")]
    public async Task CurrentRecoveryUsesTheHostContractAndRetainsItsRetryHandle(bool explicitId, string phase)
    {
        Guid operation = Guid.NewGuid(), root = Guid.NewGuid(); Guid? received = null; int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverCurrentArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverCurrent, (arguments, _) =>
            {
                Assert.AreNotEqual(Guid.Empty, arguments.OperationId);
                if (explicitId) Assert.AreEqual(operation, arguments.OperationId);
                received = arguments.OperationId; calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceInitialization(arguments.OperationId, phase, [root],
                    phase == "Completed" ? 5 : null, DateTimeOffset.UnixEpoch, phase == "Completed" ? DateTimeOffset.UnixEpoch : null,
                    phase == "Pending" ? "CurrentInventoryRecoveryRequired" : null, phase == "Completed" ? "CurrentInventory" : null));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(phase == "Completed" ? MirrorPulseControlExitCodes.Success : MirrorPulseControlExitCodes.Pending,
                await RunAsync(operations, "recover-current", explicitId ? ["--operation-id", operation.ToString("D")] : [], output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(received, data.GetProperty("operationId").GetGuid()); Assert.AreEqual(phase, data.GetProperty("phase").GetString());
            if (phase == "Completed") Assert.AreEqual("CurrentInventory", data.GetProperty("inventoryKind").GetString());
            else Assert.AreEqual("CurrentInventoryRecoveryRequired", data.GetProperty("recoveryCode").GetString());
            Assert.IsFalse(data.TryGetProperty("roleSid", out _)); Assert.IsFalse(data.TryGetProperty("catalogFingerprint", out _));
            Assert.IsFalse(data.TryGetProperty("treeReady", out _));
            foreach (string[] invalid in new string[][] { ["extra"], ["--operation-id"], ["--operation-id", "invalid"],
                ["--operation-id", Guid.Empty.ToString("D")], ["--unexpected", operation.ToString("D")] })
                Assert.AreEqual(2, await RunAsync(operations, "recover-current", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    public async Task FailedCurrentRecoveryStillExposesTheGeneratedOperationId()
    {
        Guid? received = null; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverCurrentArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverCurrent, (arguments, _) =>
            {
                received = arguments.OperationId;
                throw new IOException("Fixture current recovery response loss.");
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreNotEqual(0, await RunAsync(operations, "recover-current", [], output, error));
            Guid retry = received ?? throw new AssertFailedException("The Host did not receive the retry operation ID.");
            StringAssert.Contains(error.ToString(), retry.ToString("D")); Assert.AreEqual(string.Empty, output.ToString());
        });
    }

    [TestMethod]
    [DataRow("initialize")]
    [DataRow("recover-current")]
    public async Task WrongInventoryOperationReturnsValidationInsteadOfACompletedResult(string command)
    {
        Guid operation = Guid.NewGuid(); var dispatcher = new MirrorPulseControlDispatcher();
        if (command == "initialize")
            dispatcher.Register<NamespaceProtectionInitializeArguments, MirrorPulseControlNamespaceInitialization>(
                MirrorPulseControlCommands.NamespaceProtectionInitialize, (arguments, _) => Reject(arguments.OperationId));
        else
            dispatcher.Register<NamespaceProtectionRecoverCurrentArguments, MirrorPulseControlNamespaceInitialization>(
                MirrorPulseControlCommands.NamespaceProtectionRecoverCurrent, (arguments, _) => Reject(arguments.OperationId));
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(MirrorPulseControlExitCodes.Validation,
                await RunAsync(operations, command, ["--operation-id", operation.ToString("D")], output, error));
            Assert.AreEqual(string.Empty, output.ToString()); StringAssert.Contains(error.ToString(), operation.ToString("D"));
        });
        ValueTask<MirrorPulseControlNamespaceInitialization> Reject(Guid received)
        {
            Assert.AreEqual(operation, received);
            throw new ArgumentException("The operation ID completed a different namespace inventory.");
        }
    }
}
