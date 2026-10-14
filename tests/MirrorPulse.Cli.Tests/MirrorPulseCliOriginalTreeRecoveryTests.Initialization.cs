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
    public async Task InitializationUsesHostAdmissionAndPreservesAnExplicitOperationId(bool explicitId, string phase)
    {
        Guid operation = Guid.NewGuid(), root = Guid.NewGuid(); Guid? received = null; int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionInitializeArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionInitialize, (arguments, _) =>
            {
                Assert.AreNotEqual(Guid.Empty, arguments.OperationId); if (explicitId) Assert.AreEqual(operation, arguments.OperationId);
                received = arguments.OperationId; calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceInitialization(arguments.OperationId, phase, [root], phase == "Completed" ? 4 : null,
                    DateTimeOffset.UnixEpoch, phase == "Completed" ? DateTimeOffset.UnixEpoch.AddSeconds(1) : null,
                    phase == "Pending" ? "OriginalInventoryRecoveryRequired" : null));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(phase == "Completed" ? 0 : MirrorPulseControlExitCodes.Pending,
                await RunAsync(operations, "initialize", explicitId ? ["--operation-id", operation.ToString("D")] : [], output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(received, data.GetProperty("operationId").GetGuid());
            Assert.AreEqual(phase, data.GetProperty("phase").GetString());
            Assert.IsFalse(data.TryGetProperty("roleSid", out _)); Assert.IsFalse(data.TryGetProperty("catalogFingerprint", out _));
            Assert.IsFalse(data.TryGetProperty("treeReady", out _));
            foreach (string[] invalid in new string[][] { ["extra"], ["--operation-id"], ["--operation-id", "invalid"],
                ["--operation-id", Guid.Empty.ToString("D")], ["--unexpected", operation.ToString("D")] })
                Assert.AreEqual(2, await RunAsync(operations, "initialize", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    public async Task AFailedInitializationResponseStillReportsTheGeneratedRetryHandle()
    {
        Guid? received = null; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionInitializeArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionInitialize, (arguments, _) =>
            {
                received = arguments.OperationId;
                throw new IOException("Fixture initialization storage boundary.");
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreNotEqual(0, await RunAsync(operations, "initialize", [], output, error));
            Guid retryId = received ?? throw new AssertFailedException("The Host did not receive an operation ID.");
            StringAssert.Contains(error.ToString(), retryId.ToString("D"));
            Assert.AreEqual(string.Empty, output.ToString());
        });
    }

    [TestMethod]
    [DataRow("Pending", MirrorPulseControlExitCodes.Pending)]
    [DataRow("Completed", MirrorPulseControlExitCodes.Success)]
    public async Task InitializationStatusReportsRetainedAdmissionWithoutClaimingCurrentTreeReadiness(string phase, int expectedExit)
    {
        Guid operation = Guid.NewGuid(), root = Guid.NewGuid(); int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionInitializationStatusArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionInitializationStatus, (arguments, _) =>
            {
                Assert.AreEqual(operation, arguments.OperationId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlNamespaceInitialization(operation, phase, [root], phase == "Completed" ? 4 : null,
                    DateTimeOffset.UnixEpoch, phase == "Completed" ? DateTimeOffset.UnixEpoch.AddSeconds(1) : null));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(expectedExit, await RunAsync(operations, "initialization-status", [operation.ToString("D")], output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(phase, data.GetProperty("phase").GetString()); Assert.AreEqual(root, data.GetProperty("rootIds")[0].GetGuid());
            Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _)); Assert.IsFalse(data.TryGetProperty("roleSid", out _));
            foreach (string[] invalid in new string[][] { [], ["invalid"], [operation.ToString("D"), "extra"] })
                Assert.AreEqual(2, await RunAsync(operations, "initialization-status", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }
}
