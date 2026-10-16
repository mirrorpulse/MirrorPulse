using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseJournalUploadSourceTests
{
    [TestMethod]
    public async Task ReenableTransportSupportsProductionAcceptedUploadReadback()
    {
        var instance = InstanceId.New(); var remote = new ReenableTransport(instance);
        byte[] bytes = "accepted baseline"u8.ToArray();
        Guid operation = Guid.NewGuid(); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var intent = new MirrorPulseMutationIntent(operation, instance, "docs", MirrorPulseWorkerChangeKind.Create,
            "reenabled.txt", null, false, null, bytes.LongLength, hash, MirrorPulseMutationOrigin.Journal);
        var readback = new MirrorPulseMutationReadback(remote, remote, remote);
        Assert.IsNull(await readback.ReadMetadataAsync(intent, default));
        using var content = new MemoryStream(bytes);
        string revision = await remote.UploadAsync(new(instance, intent.RelativePath, null, content,
            bytes.LongLength, operation, hash, "docs"), default);
        var metadata = await readback.ReadMetadataAsync(intent, default);
        Assert.IsNotNull(metadata); Assert.AreEqual("reenable:file", metadata.RemoteId);
        Assert.AreEqual(bytes.LongLength, metadata.Length); Assert.AreEqual(revision, metadata.RemoteRevision);
        var accepted = new MirrorPulseMutationRecord(intent, MirrorPulseMutationState.RemoteAccepted, revision, DateTimeOffset.UtcNow);
        var proof = await readback.VerifyAsync(accepted, default);
        Assert.AreEqual(MirrorPulseMutationProofKind.Verified, proof.Kind); Assert.AreEqual(revision, proof.Revision);
        Assert.HasCount(1, remote.Uploads);
        remote.Seed("concurrent remote edit"u8.ToArray());
        Assert.AreEqual(MirrorPulseMutationProofKind.Conflict, (await readback.VerifyAsync(accepted, default)).Kind);
        Assert.HasCount(1, remote.Uploads);
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeReenabledRootUploadsOfflineEditsAgainstItsRetainedBaselineAfterRestart() =>
        RunReenabledRootScenarioAsync(remoteChanged: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeReenabledRootPreservesOfflineEditsAndAllOriginalsWhenRemoteChangedAfterRestart() =>
        RunReenabledRootScenarioAsync(remoteChanged: true);

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RunReenabledRootScenarioAsync(bool remoteChanged)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.reenable"), instance,
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        var disabled = new RootRegistration(root.AdapterId, instance, root.RootId, root.UniquenessKey, root.Label,
            root.DirectoryName, root.CustomEntry, RootRegistrationState.Disabled, root.RegisteredAt, root.IdentityScope);
        var remote = new ReenableTransport(instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid acceptedItem = Guid.Empty;
        MirrorPulseLocalFileBinding? originalBinding = null;
        Guid[] originals = [];
        string seedRevision = Convert.ToHexString(SHA256.HashData("accepted baseline"u8));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int owner = 0; owner < 2; owner++)
            {
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, [owner == 0 ? root : disabled]);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await fileSystem.StartAsync(deadline.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                await feed.StartAsync(deadline.Token);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                string file = Path.Combine(paths.SyncRootPath, "Docs", "reenabled.txt");
                var cloud = fileSystem.GetFile("Docs/reenabled.txt");
                MirrorPulseJournalUploadPump CreatePump() => new(feed, router, catalog, remote, remote, state,
                    paths.SyncRootPath, paths.DataRootPath, _ => true,
                    new MirrorPulseJournalUploadCompletion(feed, state, new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))),
                    ranges: remote, directories: remote, fileSystem: fileSystem);
                if (owner == 0)
                {
                    (await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token)).ThrowIfAnyFailed();
                    await File.WriteAllTextAsync(file, "accepted baseline", deadline.Token);
                    await using (var seedPump = CreatePump())
                    {
                        await seedPump.StartAsync(deadline.Token);
                        long? quietSince = null;
                        while (true)
                        {
                            var snapshot = await cloud.InspectAsync(deadline.Token);
                            bool accepted = remote.Uploads.Length == 1 && snapshot.IsPlaceholder && snapshot.RemoteRevision == seedRevision;
                            if (accepted && (await source.ReadPendingAsync(deadline.Token)).ReadyCommands.Count == 0)
                            {
                                quietSince ??= Stopwatch.GetTimestamp();
                                if (Stopwatch.GetElapsedTime(quietSince.Value) >= TimeSpan.FromSeconds(1))
                                {
                                    var binding = snapshot.LocalBinding!;
                                    acceptedItem = snapshot.ItemId!.Value;
                                    originalBinding = new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId);
                                    break;
                                }
                            }
                            else quietSince = null;
                            await Task.Delay(25, deadline.Token);
                        }
                    }
                    Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(remote.Uploads[0].Operation, deadline.Token))!.MayAcknowledge);
                    router.ReplaceRegistrations([disabled]);
                    await File.WriteAllTextAsync(file, "latest offline edit", deadline.Token);
                    MirrorPulseJournalUploadBatch pending;
                    while ((pending = await source.ReadPendingAsync(deadline.Token)).CompleteWindow?
                        .Any(command => command.ItemId == acceptedItem && command.Kind == MirrorPulseWorkerChangeKind.ContentUpdate) != true)
                        await Task.Delay(25, deadline.Token);
                    originals = pending.CompleteWindow!.Where(command => command.ItemId == acceptedItem)
                        .Select(command => command.OperationId).ToArray();
                    Assert.IsNotEmpty(originals); Assert.IsEmpty(pending.ReadyCommands);
                    if (remoteChanged) remote.Seed("concurrent remote edit"u8.ToArray());
                }
                int statBefore = remote.Stats;
                int callsBefore = remote.Calls;
                await using (var pump = CreatePump())
                {
                    await pump.StartAsync(deadline.Token);
                    // Observe an actual running disabled pump across several polls,
                    // including a new SDK/store owner, without touching the source.
                    await Task.Delay(600, deadline.Token);
                    Assert.AreEqual(statBefore, remote.Stats); Assert.AreEqual(callsBefore, remote.Calls); Assert.HasCount(1, remote.Uploads);
                    var pending = await source.ReadPendingAsync(deadline.Token);
                    Assert.IsEmpty(pending.ReadyCommands); Assert.IsGreaterThan(0, pending.DeferredCount);
                    await AssertOriginalsAsync(expectedPresent: true);
                    var snapshot = await cloud.InspectAsync(deadline.Token);
                    var native = snapshot.LocalBinding!;
                    Assert.AreEqual(originalBinding, new(native.VolumeSerialNumber, native.SyncRootFileId, native.LocalFileId));
                    var command = pending.CompleteWindow!.First(value => originals.Contains(value.OperationId));
                    Assert.AreEqual(seedRevision, await MirrorPulseJournalUploadRevisionGuard.ReadAcknowledgedRevisionAsync(
                        state.OpenStore, command, "Docs/reenabled.txt", cancellationToken: deadline.Token));
                    if (owner == 0) continue;

                    router.ReplaceRegistrations([root]);
                    Assert.AreEqual(root.RootId, router.Registrations.Single().RootId);
                    long? settledSince = null;
                    while (true)
                    {
                        bool settled = remoteChanged ? (await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token)).Count > 0 :
                            remote.Uploads.Length == 2 && (await source.ReadPendingAsync(deadline.Token)).ReadyCommands.Count == 0;
                        if (settled)
                        {
                            settledSince ??= Stopwatch.GetTimestamp();
                            if (Stopwatch.GetElapsedTime(settledSince.Value) >= TimeSpan.FromSeconds(1)) break;
                        }
                        else settledSince = null;
                        await Task.Delay(25, deadline.Token);
                    }
                    Assert.IsGreaterThan(statBefore, remote.Stats);
                    Assert.AreEqual("latest offline edit", await File.ReadAllTextAsync(file, deadline.Token));
                    Assert.HasCount(remoteChanged ? 1 : 2, remote.Uploads);
                    Assert.AreEqual(remoteChanged ? "concurrent remote edit" : "latest offline edit", System.Text.Encoding.UTF8.GetString(remote.Bytes!));
                    await AssertOriginalsAsync(expectedPresent: remoteChanged);
                    if (remoteChanged)
                    {
                        var conflicts = await catalog.ReadUploadConflictsAsync(cancellationToken: deadline.Token);
                        Assert.IsTrue(conflicts.All(conflict => conflict.LocalRevision == seedRevision && conflict.RemoteRevision == remote.Revision));
                        Assert.Contains(conflicts[0].ChangeId, originals.Select(id => id.ToString("D")).ToArray());
                    }
                    else
                    {
                        Assert.AreEqual(seedRevision, remote.Uploads[1].Baseline);
                        Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(remote.Uploads[1].Operation, deadline.Token))!.MayAcknowledge);
                        Assert.AreEqual(acceptedItem, (await cloud.InspectAsync(deadline.Token)).ItemId);
                    }
                    TestContext.WriteLine($"ReenabledRoot: twoSdkOwners=True; stableRootAndNativeBinding=True; disabledSourceCalls=0; " +
                        $"originalCount={originals.Length}; retainedBaseline=True; remoteChanged={remoteChanged}; " +
                        $"conditionalUpload={!remoteChanged}; preservedConflict={remoteChanged}; strictHostIntegration=False; transportFixture=True.");
                }

                async Task AssertOriginalsAsync(bool expectedPresent)
                {
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (Guid id in originals)
                        Assert.AreEqual(expectedPresent, await transaction.Operations.GetAsync(id, deadline.Token) is not null);
                    await transaction.RollbackAsync(deadline.Token);
                }
            }
        }
        finally
        {
            string log = Path.Combine(paths.DataRootPath, "logs", "mirrorpulse.log");
            if (File.Exists(log)) foreach (string line in File.ReadLines(log).TakeLast(12)) TestContext.WriteLine(line);
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ReenableTransport(InstanceId instance) : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerRangeTransport
    {
        private int _stats;
        private int _calls;
        public int Stats => Volatile.Read(ref _stats);
        public int Calls => Volatile.Read(ref _calls);
        public byte[]? Bytes { get; private set; }
        public string? Revision => Bytes is null ? null : Convert.ToHexString(SHA256.HashData(Bytes));
        private readonly System.Collections.Concurrent.ConcurrentQueue<(Guid Operation, string? ExpectedRevision)> _uploads = new();
        public (Guid Operation, string? Baseline)[] Uploads => _uploads.ToArray();
        public void Seed(byte[] bytes) => Bytes = bytes;
        private void Require(InstanceId actual, string? rootKey, string path)
        { Assert.AreEqual(instance, actual); Assert.AreEqual("docs", rootKey); Assert.AreEqual("reenabled.txt", path); }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls); Require(request.InstanceId, request.RootKey, request.NormalizedPath); Interlocked.Increment(ref _stats); return ValueTask.FromResult(Revision); }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls);
            Require(request.InstanceId, request.RootKey, "reenabled.txt");
            Assert.AreEqual(string.Empty, request.NormalizedPath); Assert.IsTrue(request.ContinuationCursor.IsEmpty);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(Bytes is null ? [] :
                [new("reenable:file", Revision!, "File", "reenabled.txt", Bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls); Require(request.InstanceId, request.RootKey, request.NormalizedPath);
            return ValueTask.FromResult<Stream>(new MemoryStream(Bytes!.AsSpan(checked((int)request.Offset), checked((int)request.Length)).ToArray()));
        }
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Require(request.InstanceId, request.RootKey, request.NormalizedPath);
            Assert.AreEqual(request.ExpectedRevision, Revision); Assert.IsNotNull(request.OperationId);
            using var output = new MemoryStream(); await request.Content.CopyToAsync(output, token);
            byte[] bytes = output.ToArray(); Assert.AreEqual(request.Length, bytes.LongLength);
            Assert.AreEqual(request.ExpectedContentSha256, Convert.ToHexString(SHA256.HashData(bytes)));
            Bytes = bytes; _uploads.Enqueue((request.OperationId.Value, request.ExpectedRevision)); return Revision!;
        }
    }
}
