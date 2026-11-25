using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.26100")]
[TestClass]
public sealed class MirrorPulseReadOnlyHydrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task ReadOnlyHydrationDoesNotPublishAContentUpdate()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();

        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        byte[] expected = new byte[65_537];
        new Random(20261125).NextBytes(expected);
        var provider = new FixtureContent(expected);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths)
                .WithStateStore(state).WithContentProvider(provider).Build();
            await fileSystem.StartAsync(token);
            var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "hydration-fixture-file", "first");
            (await fileSystem.Root.CreatePlaceholdersAsync([
                CloudFilePlaceholderSpec.CreateBuilder("readback.bin", identity, expected.Length).WithInSyncState(true).Build()],
                cancellationToken: token)).ThrowIfAnyFailed();

            await using var feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(token);
            var file = fileSystem.GetFile("readback.bin");
            Assert.HasCount(0, await ReadContentJournalAsync(state, token));
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(file.FullPath, token));
            Assert.IsGreaterThan(0, provider.Reads, "The ordinary read must fetch online-only bytes from CfSharp.");
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            var afterRead = await ReadContentJournalAsync(state, token);

            // An ordinary real same-length write is the positive control for an active subscription.
            await using (var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                writer.WriteByte((byte)(expected[0] ^ 0xff));
                await writer.FlushAsync(token);
            }
            var beforeWriteIds = afterRead.Select(row => row.OperationId).ToHashSet();
            var writeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            CloudOperationJournalEntry[] afterWrite;
            do
            {
                afterWrite = await ReadContentJournalAsync(state, token);
                if (afterWrite.Any(row => !beforeWriteIds.Contains(row.OperationId))) break;
                await Task.Delay(TimeSpan.FromMilliseconds(125), token);
            } while (DateTimeOffset.UtcNow < writeDeadline);

            TestContext.WriteLine($"HydrationReadback: cfSharp={typeof(CloudFileSystem).Assembly.GetName().Version}; "
                + $"callbacks={provider.Reads}; readOnlyContentObservations={afterRead.Length}; "
                + $"contentObservationsAfterOrdinaryWrite={afterWrite.Length}.");
            Assert.IsTrue(afterWrite.Any(row => !beforeWriteIds.Contains(row.OperationId)),
                "The control write must contribute a new durable content observation.");
            Assert.IsEmpty(afterRead, "Hydration through an exclusively read-only handle published durable ContentUpdate.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<CloudOperationJournalEntry[]> ReadContentJournalAsync(MirrorPulseCfSharpStateSession state, CancellationToken token)
    {
        await using var read = await state.OpenStore.BeginTransactionAsync(token);
        var records = await read.Operations.ListAsync(64, token);
        await read.RollbackAsync(token);
        return records.Where(row => row.Kind == CloudStateOperationKind.ContentUpdate).ToArray();
    }

    private sealed class FixtureContent(byte[] content) : ICloudFileContentProvider
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);

        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            return ValueTask.FromResult<Stream>(new MemoryStream(content.AsSpan(checked((int)request.Offset),
                checked((int)request.Length)).ToArray(), writable: false));
        }
    }
}
