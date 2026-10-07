using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseDirectoryJournalTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeOfflineEmptyAndNestedDirectoryJournalResumesAfterRuntimeRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.directory"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var remote = new DirectoryTransport(Path.Combine(root, "remote"));
        Guid[] operations = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int run = 0; run < 2; run++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await fileSystem.StartAsync(timeout.Token);
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var completion = new MirrorPulseJournalUploadCompletion(feed, state,
                    new BackoffPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)));
                int currentRun = run;
                await using var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, remote, remote, state,
                    paths.SyncRootPath, paths.DataRootPath, _ => currentRun == 1, completion,
                    mutations: remote, directories: remote, fileSystem: fileSystem);
                await pump.StartAsync(timeout.Token);
                if (run == 0)
                {
                    Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs", "empty"));
                    Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs", "parent", "nested"));
                    while (operations.Length < 3)
                    {
                        CloudLocalChangeBatch batch = await feed.ReadBatchAsync(timeout.Token);
                        operations = batch.Changes.Where(change => change.IsDirectory && change.Kind == CloudLocalChangeKind.Create)
                            .Select(change => change.OperationId).ToArray();
                        if (operations.Length < 3) await Task.Delay(20, timeout.Token);
                    }
                    Assert.AreEqual(0, remote.Creates);
                    Assert.IsFalse(Directory.Exists(remote.Root));
                }
                else
                {
                    while (true)
                    {
                        MirrorPulseMutationRecord?[] records = await Task.WhenAll(operations.Select(id => catalog.ReadMutationAsync(id, timeout.Token)));
                        if (records.All(record => record?.State == MirrorPulseMutationState.Acknowledged)) break;
                        await Task.Delay(20, timeout.Token);
                    }
                    Assert.AreEqual(3, remote.Creates);
                    Assert.IsTrue(Directory.Exists(Path.Combine(remote.Root, "empty")));
                    Assert.IsTrue(Directory.Exists(Path.Combine(remote.Root, "parent", "nested")));
                    CloudItemSnapshot snapshot = await fileSystem.GetDirectory("Docs/empty").InspectAsync(timeout.Token);
                    Assert.IsTrue(snapshot.IsPlaceholder);
                    Assert.AreEqual(router.CreateFileIdentity(instance, "docs", "remote:empty", "directory:empty").ItemId,
                        CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span).ItemId);
                    Assert.IsTrue(pump.Health.Healthy);
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class DirectoryTransport(string root) : IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerUploadTransport
    {
        public string Root { get; } = root;
        public int Creates { get; private set; }
        public ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request, CancellationToken cancellationToken)
        {
            Assert.IsTrue(request.MustBeAbsent);
            string path = Path.Combine(Root, request.NormalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path)) throw new MirrorPulseWorkerMutationConflictException(null, "exists");
            Directory.CreateDirectory(path);
            Creates++;
            return ValueTask.FromResult("directory:" + request.NormalizedPath.Replace('\\', '/'));
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Directory.Exists(Path.Combine(Root, request.NormalizedPath))
                ? "directory:" + request.NormalizedPath.Replace('\\', '/') : null);
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken)
        {
            string parent = Path.Combine(Root, request.NormalizedPath);
            MirrorPulseWorkerDirectoryEntry[] entries = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent)
                .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
                .Select(path => new MirrorPulseWorkerDirectoryEntry("remote:" + path, "directory:" + path, "Directory", path, null, null, null, false)).ToArray() : [];
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(entries, ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken) => throw new AssertFailedException("Unexpected deletion.");
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken) => throw new AssertFailedException("Unexpected move.");
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken) => throw new AssertFailedException("Empty directories have no file upload.");
    }
}
