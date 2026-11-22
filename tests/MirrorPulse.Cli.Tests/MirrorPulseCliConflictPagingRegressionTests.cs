using System.Text.Json;
using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Cli.Tests;

// Compile the actual Host query source into this boundary test assembly.
// Requests cross the production current-user pipe; no Cloud Files Host starts.
[TestClass]
public sealed class MirrorPulseCliConflictPagingRegressionTests
{
    [TestMethod]
    public async Task OriginalFirstConflictPageStillReadsBothRealCatalogFamilies()
    {
        await using var fixture = await Fixture.OpenAsync();
        var first = await fixture.Client.GetConflictsAsync(limit: 2);
        CollectionAssert.AreEqual(fixture.NewestFirst.Take(2).ToArray(), first.Items.Select(item => item.ConflictId).ToArray());
        Assert.IsNotNull(first.NextCursor);
        Assert.AreEqual(2, first.Items.Select(item => item.Source).Distinct().Count());
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    public async Task ReturnedConflictCursorMustAdvanceTheActualHostQueryAcrossItsCatalog(int count)
    {
        await using var fixture = await Fixture.OpenAsync();
        var first = await fixture.Client.GetConflictsAsync(limit: count);
        var second = await fixture.Client.GetConflictsAsync(limit: count, cursor: first.NextCursor);
        Console.WriteLine($"ConflictPagingActual: pageSize={count}; firstCount={first.Items.Count}; secondCount={second.Items.Count}; " +
            $"samePage={first.Items.SequenceEqual(second.Items)}; nativeExecuted=False; hostProcessStarted=False.");
        CollectionAssert.AreEqual(fixture.NewestFirst.Skip(count).Take(count).ToArray(), second.Items.Select(item => item.ConflictId).ToArray(),
            "The actual Host query must apply the cursor it returned, rather than repeat the first page.");
        Assert.IsEmpty(first.Items.Select(item => item.ConflictId).Intersect(second.Items.Select(item => item.ConflictId)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CliShowMustFindAKnownConflictAfterTheFirstHundredRecords(bool json)
    {
        await using var fixture = await Fixture.OpenAsync();
        string wanted = fixture.NewestFirst[^1];
        string[] arguments = json
            ? ["--no-start", "--json", "conflict", "show", "--conflict-id", wanted]
            : ["--no-start", "conflict", "show", "--conflict-id", wanted];
        using var output = new StringWriter(); using var error = new StringWriter();
        int exit = await MirrorPulseCliApplication.RunAsync(arguments, output, error,
            hostOperations: fixture.Operations, isPlatformSupported: () => true);
        Assert.AreEqual(0, exit); Assert.AreEqual(string.Empty, error.ToString());
        if (json)
        {
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual(wanted, document.RootElement.GetProperty("data").GetProperty("conflictId").GetString());
        }
        else StringAssert.Contains(output.ToString(), wanted);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly MirrorPulseProductCatalog _catalog;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        internal MirrorPulseStoragePaths Paths { get; }
        internal MirrorPulseControlClient Client { get; }
        internal MirrorPulseCliHostOperations Operations { get; }
        internal IReadOnlyList<string> NewestFirst { get; }

        private Fixture(string root, MirrorPulseStoragePaths paths, MirrorPulseProductCatalog catalog, IReadOnlyList<string> order)
        {
            _root = root; Paths = paths; _catalog = catalog; NewestFirst = order;
            var dispatcher = new MirrorPulseControlDispatcher();
            dispatcher.Register<ConflictListArguments, MirrorPulseControlConflictList>(MirrorPulseControlCommands.ConflictList,
                async (arguments, token) => await MirrorPulse.Host.MirrorPulseConflictQueries.ReadAsync(catalog, arguments, token));
            string pipe = "MirrorPulse-conflict-paging-" + Guid.NewGuid().ToString("N");
            _serve = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe).ServeAsync(_shutdown.Token);
            Client = new(new MirrorPulseControlClientOptions { PipeName = pipe });
            Operations = new(new MirrorPulseHostStartupOptions(), Client);
        }

        internal static async Task<Fixture> OpenAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-paging-" + Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var ids = new List<string>(); var instance = InstanceId.New();
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            for (int index = 0; index < 106; index++)
            {
                var record = new MirrorPulseConflictRecord(Guid.NewGuid(), instance, "synthetic-" + index,
                    "Docs/synthetic-" + index + ".txt", MirrorPulseConflictReason.Content, MirrorPulseVersionComparison.Unknown,
                    "local", "remote", start.AddTicks(index),
                    source: index % 2 == 0 ? MirrorPulseConflictSource.Upload : MirrorPulseConflictSource.CfSharpRemote);
                if (record.Source == MirrorPulseConflictSource.Upload) await catalog.SaveUploadConflictAsync(record);
                else await catalog.SaveRemoteConflictProjectionAsync(record);
                ids.Add(record.ConflictId.ToString("D"));
            }
            ids.Reverse(); return new(root, paths, catalog, ids);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel(); await _serve; await Operations.DisposeAsync();
            await _catalog.DisposeAsync(); _shutdown.Dispose();
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(temporary + "MirrorPulse-conflict-paging-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The owned paging fixture escaped its temporary parent.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
