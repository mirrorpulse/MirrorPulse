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
    public async Task SelectedRootRecoveryPreservesScopeAndRetryHandleThroughTheHostPipe(bool explicitId, string phase)
    {
        Guid operation = Guid.NewGuid(), root = Guid.NewGuid(); Guid? received = null; int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverRootArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverRoot, (arguments, _) =>
            {
                Assert.AreEqual(root, arguments.RootId); Assert.AreNotEqual(Guid.Empty, arguments.OperationId);
                if (explicitId) Assert.AreEqual(operation, arguments.OperationId);
                received = arguments.OperationId; calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceInitialization(arguments.OperationId, phase, [root],
                    phase == "Completed" ? 3 : null, DateTimeOffset.UnixEpoch, phase == "Completed" ? DateTimeOffset.UnixEpoch : null,
                    phase == "Pending" ? "RootInventoryRecoveryRequired" : null, "CurrentInventory", root));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = explicitId ? [root.ToString("D"), "--operation-id", operation.ToString("D")] : [root.ToString("D")];
            Assert.AreEqual(phase == "Completed" ? MirrorPulseControlExitCodes.Success : MirrorPulseControlExitCodes.Pending,
                await RunAsync(operations, "recover-root", arguments, output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(received, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual(root, data.GetProperty("scopeRootId").GetGuid());
            Assert.AreEqual(phase, data.GetProperty("phase").GetString());
            Assert.AreEqual("CurrentInventory", data.GetProperty("inventoryKind").GetString());
            if (phase == "Pending") Assert.AreEqual("RootInventoryRecoveryRequired", data.GetProperty("recoveryCode").GetString());
            Assert.IsFalse(data.TryGetProperty("roleSid", out _)); Assert.IsFalse(data.TryGetProperty("catalogFingerprint", out _));
            Assert.IsFalse(data.TryGetProperty("treeReady", out _)); Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _));
            foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [root.ToString("D"), "extra"],
                [root.ToString("D"), "--operation-id"], [root.ToString("D"), "--operation-id", "invalid"],
                [root.ToString("D"), "--operation-id", Guid.Empty.ToString("D")], [root.ToString("D"), "--unexpected", operation.ToString("D")],
                [root.ToString("D"), "--operation-id", operation.ToString("D"), "--operation-id", operation.ToString("D")] })
                Assert.AreEqual(MirrorPulseControlExitCodes.Validation, await RunAsync(operations, "recover-root", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    public async Task FailedSelectedRootRecoveryStillExposesTheGeneratedOperationId()
    {
        Guid root = Guid.NewGuid(); Guid? received = null; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverRootArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverRoot, (arguments, _) =>
            {
                Assert.AreEqual(root, arguments.RootId); received = arguments.OperationId;
                throw new IOException("Fixture selected-root response loss.");
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreNotEqual(0, await RunAsync(operations, "recover-root", [root.ToString("D")], output, error));
            Guid retry = received ?? throw new AssertFailedException("The Host did not receive the selected-root retry operation ID.");
            StringAssert.Contains(error.ToString(), retry.ToString("D")); Assert.AreEqual(string.Empty, output.ToString());
        });
    }

    [TestMethod]
    public async Task WrongSelectedRootOperationReturnsValidationWithoutACompletedResult()
    {
        Guid operation = Guid.NewGuid(), root = Guid.NewGuid(); var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverRootArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverRoot, (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId); Assert.AreEqual(root, arguments.RootId);
                throw new ArgumentException("The retained operation belongs to another root or a whole inventory.");
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(MirrorPulseControlExitCodes.Validation, await RunAsync(operations, "recover-root",
                [root.ToString("D"), "--operation-id", operation.ToString("D")], output, error));
            Assert.AreEqual(string.Empty, output.ToString()); StringAssert.Contains(error.ToString(), operation.ToString("D"));
        });
    }
}
