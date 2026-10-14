using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliOriginalTreeRecoveryTests
{
    [TestMethod]
    [DataRow("Capturing")]
    [DataRow("Sealed")]
    [DataRow("Cancelled")]
    public async Task CaptureQueryPreservesBoundedCursorAndHistoricalPhase(string phase)
    {
        Guid root = Guid.NewGuid(), cursor = Guid.NewGuid(), manifest = Guid.NewGuid(); int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionCapturesArguments, IReadOnlyList<MirrorPulseControlPermissionCapture>>(
            MirrorPulseControlCommands.NamespaceProtectionCaptures, (arguments, _) =>
            {
                Assert.AreEqual(root, arguments.RootId); Assert.AreEqual(cursor, arguments.AfterManifestId);
                Assert.AreEqual(256, arguments.Limit); calls++;
                return ValueTask.FromResult<IReadOnlyList<MirrorPulseControlPermissionCapture>>(
                    [new(manifest, root, phase, 2, 3, DateTimeOffset.UnixEpoch, phase == "Sealed" ? DateTimeOffset.UnixEpoch : null)]);
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(0, await RunAsync(operations, "captures", [root.ToString("D"), "--after", cursor.ToString("D")], output, error));
            using var json = JsonDocument.Parse(output.ToString()); var first = json.RootElement.GetProperty("data")[0];
            Assert.AreEqual(phase, first.GetProperty("phase").GetString()); Assert.AreEqual(manifest, first.GetProperty("manifestId").GetGuid());
            Assert.IsFalse(first.TryGetProperty("ownerSid", out _)); Assert.IsFalse(first.TryGetProperty("dacl", out _));
            foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")],
                [root.ToString("D"), "--after"], [root.ToString("D"), "--after", "invalid"] })
                Assert.AreEqual(2, await RunAsync(operations, "captures", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }

    [TestMethod]
    [DataRow("Verified", 0)]
    [DataRow("RecoveryRequired", MirrorPulseControlExitCodes.Pending)]
    public async Task OriginalRecoveryReportsItsActualOutcomeWithoutGrantingTreeReadiness(string outcome, int expectedExit)
    {
        Guid manifest = Guid.NewGuid(); int calls = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<NamespaceProtectionRecoverTreeArguments, MirrorPulseControlOriginalTreeRecovery>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverTree, (arguments, _) =>
            {
                Assert.AreEqual(manifest, arguments.ManifestId); calls++;
                return ValueTask.FromResult(new MirrorPulseControlOriginalTreeRecovery(manifest, outcome, 3,
                    DateTimeOffset.UnixEpoch, outcome == "Verified" ? null : "ObjectChanged"));
            });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(expectedExit, await RunAsync(operations, "recover-tree", [manifest.ToString("D")], output, error));
            using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(outcome, data.GetProperty("outcome").GetString()); Assert.IsFalse(data.TryGetProperty("treeReady", out _));
            Assert.IsFalse(data.TryGetProperty("remoteAccepted", out _));
            foreach (string[] invalid in new string[][] { [], ["invalid"], [Guid.Empty.ToString("D")], [manifest.ToString("D"), "extra"] })
                Assert.AreEqual(2, await RunAsync(operations, "recover-tree", invalid, TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(1, calls);
        });
    }

    private static async Task WithPipeAsync(MirrorPulseControlDispatcher dispatcher, Func<MirrorPulseCliHostOperations, Task> test)
    {
        string pipe = "MirrorPulse-original-tree-cli-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serve = server.ServeAsync(stop.Token);
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(),
            new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe }));
        try { await test(operations); }
        finally { stop.Cancel(); await serve; }
    }

    private static Task<int> RunAsync(MirrorPulseCliHostOperations operations, string verb, string[] arguments, TextWriter output, TextWriter error) =>
        MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "namespace", "protection", verb, .. arguments], output, error,
            hostOperations: operations, isPlatformSupported: () => true);
}
