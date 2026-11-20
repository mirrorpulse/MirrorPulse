using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliConflictVersionRefreshTests
{
    private static readonly bool[] OutputModes = [false, true];

    [TestMethod]
    [DataRow("pending", 11)]
    [DataRow("resolved", 0)]
    public async Task CliPassesTheSameExplicitRequestThroughTheRealPipeAndReportsOnlyItsActualOutcome(string state, int expectedExit)
    {
        Guid command = Guid.NewGuid(), conflict = Guid.NewGuid(); int calls = 0;
        var expected = Outcome(command, conflict, state); var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ConflictRefreshVersionArguments, MirrorPulseControlConflictVersionRefresh>(MirrorPulseControlCommands.ConflictRefreshVersion,
            (arguments, _) => { Assert.AreEqual(command, arguments.CommandId); Assert.AreEqual(conflict, arguments.ConflictId); calls++; return ValueTask.FromResult(expected); });
        await WithPipeAsync(dispatcher, async operations =>
        {
            foreach (bool json in OutputModes)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                string[] prefix = json ? ["--no-start", "--json"] : ["--no-start"];
                string[] arguments = [.. prefix, "conflict", "refresh-version", "--conflict-id", conflict.ToString("D"), "--operation-id", command.ToString("D")];
                Assert.AreEqual(expectedExit, await MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
                Assert.AreEqual(string.Empty, error.ToString());
                if (json)
                {
                    using var document = JsonDocument.Parse(output.ToString()); var data = document.RootElement.GetProperty("data");
                    Assert.AreEqual(command, data.GetProperty("commandId").GetGuid()); Assert.AreEqual(conflict, data.GetProperty("conflictId").GetGuid());
                    Assert.AreEqual(state, data.GetProperty("state").GetString());
                    if (state == "resolved")
                    {
                        var observed = data.GetProperty("observation");
                        Assert.IsFalse(observed.TryGetProperty("originalErrorActualRevision", out var errorRevision) && errorRevision.ValueKind != JsonValueKind.Null);
                        Assert.AreEqual("observed-current", observed.GetProperty("selectedRemoteRevision").GetString());
                        Assert.AreEqual("request-baseline", observed.GetProperty("originalRequestExpectedRevision").GetString());
                    }
                }
                else if (state == "pending") StringAssert.Contains(output.ToString(), command.ToString("D"));
            }
        });
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow("--conflict-id|{conflict}")]
    [DataRow("--operation-id|{command}")]
    [DataRow("--conflict-id|invalid|--operation-id|{command}")]
    [DataRow("--conflict-id|{conflict}|--operation-id|00000000-0000-0000-0000-000000000000")]
    [DataRow("--conflict-id|{conflict}|--conflict-id|{command}")]
    [DataRow("--conflict-id|{conflict}|--unknown|{command}")]
    public async Task CliRefusesAmbiguousOrMissingExplicitIdsBeforeContactingTheHost(string encoded)
    {
        int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ConflictRefreshVersionArguments, MirrorPulseControlConflictVersionRefresh>(MirrorPulseControlCommands.ConflictRefreshVersion,
            (arguments, _) => { calls++; return ValueTask.FromResult(Outcome(arguments.CommandId, arguments.ConflictId, "pending")); });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] options = encoded.Replace("{command}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal)
                .Replace("{conflict}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal).Split('|');
            string[] arguments = ["--no-start", "--json", "conflict", "refresh-version", .. options];
            Assert.AreEqual(2, await MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
            Assert.AreEqual(string.Empty, output.ToString()); Assert.IsFalse(string.IsNullOrEmpty(error.ToString()));
        });
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("command")]
    [DataRow("conflict")]
    [DataRow("resolved-without-observation")]
    [DataRow("pending-with-observation")]
    [DataRow("state")]
    [DataRow("observation-id")]
    [DataRow("duplicate-epochs")]
    public async Task CliRejectsSubstitutedOrInconsistentHostOutcomes(string changed)
    {
        Guid command = Guid.NewGuid(), conflict = Guid.NewGuid(); var dispatcher = new MirrorPulseControlDispatcher(); var valid = Outcome(command, conflict, "resolved");
        var malformed = changed switch
        {
            "version" => valid with { Version = 2 },
            "command" => valid with { CommandId = Guid.NewGuid() },
            "conflict" => valid with { ConflictId = Guid.NewGuid() },
            "resolved-without-observation" => valid with { Observation = null },
            "pending-with-observation" => valid with { State = "pending" },
            "state" => valid with { State = "unknown" },
            "observation-id" => valid with { Observation = valid.Observation! with { ObservationId = Guid.Empty } },
            _ => valid with { Observation = valid.Observation! with { SelectedConflictIds = [conflict, conflict] } }
        };
        dispatcher.Register<ConflictRefreshVersionArguments, MirrorPulseControlConflictVersionRefresh>(MirrorPulseControlCommands.ConflictRefreshVersion,
            (_, _) => ValueTask.FromResult(malformed));
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = ["--no-start", "conflict", "refresh-version", "--conflict-id", conflict.ToString("D"), "--operation-id", command.ToString("D")];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
        });
    }

    [TestMethod]
    public async Task AnOlderHostReportsUnsupportedWithoutFallingBackToAnotherConflictAction()
    {
        var dispatcher = new MirrorPulseControlDispatcher();
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = ["--no-start", "--json", "conflict", "refresh-version", "--conflict-id", Guid.NewGuid().ToString("D"), "--operation-id", Guid.NewGuid().ToString("D")];
            Assert.AreEqual(MirrorPulseControlExitCodes.Unsupported, await MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
            StringAssert.Contains(error.ToString(), MirrorPulseControlErrorCodes.UnknownCommand);
        });
    }

    private static MirrorPulseControlConflictVersionRefresh Outcome(Guid command, Guid conflict, string state)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(1, command, conflict, state, state == "pending" ? null :
            new(1, Guid.NewGuid(), Guid.NewGuid(), "request-baseline", null, null, "observed-current", now, now, null, [Guid.NewGuid(), Guid.NewGuid()]));
    }

    private static async Task WithPipeAsync(MirrorPulseControlDispatcher dispatcher, Func<MirrorPulseCliHostOperations, Task> action)
    {
        string pipe = "MirrorPulse-refresh-cli-" + Guid.NewGuid().ToString("N"); using var shutdown = new CancellationTokenSource();
        Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try { await action(operations); } finally { shutdown.Cancel(); await serve; }
    }
}
