using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliConflictPagingOptionsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ListPassesBoundedLimitAndOpaqueCursorThroughThePipeAndReportsTheNextCursor(bool json)
    {
        int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ConflictListArguments, MirrorPulseControlConflictList>(MirrorPulseControlCommands.ConflictList, (arguments, _) =>
        {
            Assert.AreEqual(7, arguments.Limit); Assert.AreEqual("opaque-original", arguments.Cursor); calls++;
            return ValueTask.FromResult(new MirrorPulseControlConflictList([], "opaque-next"));
        });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] prefix = json ? ["--no-start", "--json"] : ["--no-start"];
            string[] arguments = [.. prefix, "conflict", "list", "--limit=7", "--cursor", "opaque-original"];
            Assert.AreEqual(0, await MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
            Assert.AreEqual(string.Empty, error.ToString());
            if (json)
            {
                using var document = JsonDocument.Parse(output.ToString());
                Assert.AreEqual("opaque-next", document.RootElement.GetProperty("data").GetProperty("nextCursor").GetString());
            }
            else StringAssert.Contains(output.ToString(), "opaque-next");
        });
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("--limit|0")]
    [DataRow("--limit|1001")]
    [DataRow("--limit|invalid")]
    [DataRow("--limit")]
    [DataRow("--limit|1|--limit|2")]
    [DataRow("--cursor|")]
    [DataRow("--cursor|first|--cursor|second")]
    [DataRow("--unknown|value")]
    public async Task InvalidListOptionsAreRejectedBeforeAnyHostRequest(string encoded)
    {
        int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ConflictListArguments, MirrorPulseControlConflictList>(MirrorPulseControlCommands.ConflictList, (_, _) =>
        { calls++; return ValueTask.FromResult(new MirrorPulseControlConflictList([])); });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = ["--no-start", "--json", "conflict", "list", .. encoded.Split('|')];
            Assert.AreEqual(2, await MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
            Assert.AreEqual(string.Empty, output.ToString()); Assert.IsFalse(string.IsNullOrEmpty(error.ToString()));
        });
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShowRejectsARepeatedOrCyclicHostCursorWithoutAnotherRequest(bool cycle)
    {
        int calls = 0; var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ConflictListArguments, MirrorPulseControlConflictList>(MirrorPulseControlCommands.ConflictList, (_, _) =>
        {
            calls++; return ValueTask.FromResult(new MirrorPulseControlConflictList([], cycle && calls == 2 ? "second" : "first"));
        });
        await WithPipeAsync(dispatcher, async operations =>
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            string[] arguments = ["--no-start", "conflict", "show", "--conflict-id", Guid.NewGuid().ToString("D")];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseCliApplication.RunAsync(arguments, output, error, operations, () => true));
        });
        Assert.AreEqual(cycle ? 3 : 2, calls);
    }

    private static async Task WithPipeAsync(MirrorPulseControlDispatcher dispatcher, Func<MirrorPulseCliHostOperations, Task> action)
    {
        string pipe = "MirrorPulse-conflict-options-" + Guid.NewGuid().ToString("N"); using var shutdown = new CancellationTokenSource();
        Task serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions { PipeName = pipe });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try { await action(operations); } finally { shutdown.Cancel(); await serve; }
    }
}
