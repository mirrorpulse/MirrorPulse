using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseJournalUploadSourceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpCoalescesContentAndRecoversAllOriginalAcknowledgementsAfterRestart() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpResolvesAGroupConflictWithADistinctAuthorizedAttemptAndRecoversItsOriginalAcknowledgements() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: true);

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RunProductionCoalescingScenarioAsync(bool resolveConflict)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); InstanceId instance = InstanceId.New();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.coalescing"), instance,
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var remote = new CoalescingTransport(instance) { RejectFirstUpload = resolveConflict };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid planId = Guid.Empty; Guid[] originals = []; Guid effectId = Guid.Empty;
        Guid? retryCommandId = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                await feed.StartAsync(timeout.Token);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                string file = Path.Combine(paths.SyncRootPath, "Docs", "coalesced.txt");
                if (owner == 0)
                {
                    (await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token)).ThrowIfAnyFailed();
                    await File.WriteAllTextAsync(file, "first local edit", timeout.Token);
                    var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    MirrorPulseJournalUploadBatch window;
                    while (!(window = await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.RelativePath == "coalesced.txt"))
                        await Task.Delay(25, timeout.Token);
                    Guid first = window.ReadyCommands.First(command => command.RelativePath == "coalesced.txt").OperationId;
                    await File.WriteAllTextAsync(file, "final coalesced bytes", timeout.Token);
                    while ((window = await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Count(command => command.RelativePath == "coalesced.txt") < 2)
                        await Task.Delay(25, timeout.Token);
                    Assert.IsNotNull(window.CompleteWindow); Assert.Contains(first, window.ReadyCommands.Select(command => command.OperationId).ToArray());
                }
                await feed.DisposeAsync();
                await using var pumpFeed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                var completion = new MirrorPulseJournalUploadCompletion(pumpFeed, state,
                    new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var center = new MirrorPulseConflictCenter();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using (var pump = new MirrorPulseJournalUploadPump(pumpFeed, router, catalog, remote, remote, state,
                    paths.SyncRootPath, paths.DataRootPath, _ => true, completion, conflicts: center, mutations: remote, ranges: remote,
                    directories: remote, fileSystem: fileSystem, scheduler: scheduler)
                {
                    CoalescingCheckpoint = (checkpoint, _) =>
                    {
                        if (owner == 0 && checkpoint == MirrorPulseJournalCoalescingCheckpoint.AfterEffectsCompleted)
                        { reached.TrySetResult(); throw new IOException("Synthetic cut before original journal acknowledgement."); }
                        if (owner == 1 && checkpoint == MirrorPulseJournalCoalescingCheckpoint.AfterOriginalsAcknowledged) reached.TrySetResult();
                        return ValueTask.CompletedTask;
                    }
                })
                {
                    await pump.StartAsync(timeout.Token);
                    if (owner == 0 && resolveConflict)
                    {
                        MirrorPulseConflictRecord[] conflicts;
                        while ((conflicts = (await catalog.ReadUploadConflictsAsync(cancellationToken: timeout.Token)).ToArray()).Length < 2)
                            await Task.Delay(25, timeout.Token);
                        var actions = new MirrorPulseUploadConflictActions(catalog, state, pumpFeed,
                            new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), paths, router, remote, center, scheduler);
                        var decision = await actions.ApplyAsync(conflicts[1].ConflictId, MirrorPulseConflictAction.KeepLocal, timeout.Token);
                        retryCommandId = decision.ResolutionId;
                        Assert.IsEmpty(center.Query()); Assert.IsEmpty(await catalog.ReadUploadConflictsAsync(cancellationToken: timeout.Token));
                    }
                    await reached.Task.WaitAsync(timeout.Token);
                    var plan = (await catalog.ReadJournalCoalescingPlansAsync(timeout.Token)).Single();
                    var execution = (await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, timeout.Token))!;
                    Assert.AreEqual(MirrorPulseCoalescedEffect.CreateFile, plan.Effect); Assert.HasCount(1, execution.Steps);
                    var current = await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0, timeout.Token);
                    if (owner == 0)
                    { planId = plan.PlanId; originals = plan.Members.Select(member => member.OperationId).ToArray(); effectId = current.Intent.OperationId; }
                    else { Assert.AreEqual(planId, plan.PlanId); Assert.AreEqual(effectId, current.Intent.OperationId); }
                    if (resolveConflict)
                    {
                        Assert.AreNotEqual(execution.Steps[0].OperationId, effectId);
                        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(execution.Steps[0].OperationId, timeout.Token))!.State);
                        if (owner == 1)
                        {
                            while ((await catalog.ReadUserCommandAsync(retryCommandId!.Value, timeout.Token))?.State != "resolved")
                                await Task.Delay(25, timeout.Token);
                        }
                    }
                    Assert.IsFalse(originals.Contains(effectId)); Assert.HasCount(1, remote.UploadedOperations);
                    Assert.AreEqual(effectId, remote.UploadedOperations[0]);
                    Assert.AreEqual("final coalesced bytes", System.Text.Encoding.UTF8.GetString(remote.Bytes!));
                    Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await catalog.ReadMutationAsync(effectId, timeout.Token))!.State);
                    Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(effectId, timeout.Token))!.MayAcknowledge);
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                    foreach (Guid id in originals)
                        Assert.AreEqual(owner == 0, await transaction.Operations.GetAsync(id, timeout.Token) is not null);
                    await transaction.RollbackAsync(timeout.Token);
                    TestContext.WriteLine($"ProductionCoalescing: owner={owner}; originalCount={originals.Length}; separateEffectId=True; " +
                        "remoteUploads=1; actualNativeConfirmation=True; officialProjection=True; originalIdsRetainedAtCut=True; " +
                        $"reopenedOwnerDoesNotRedispatch=True; wholeOriginalGroupAcknowledged=True; authorizedGroupRetry={resolveConflict}; strictHostIntegration=False.");
                }
            }
        }
        finally
        {
            string log = Path.Combine(paths.DataRootPath, "logs", "mirrorpulse.log");
            if (File.Exists(log)) foreach (string line in File.ReadLines(log).TakeLast(12)) TestContext.WriteLine(line);
            registry.Unregister(paths.SyncRootPath); if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class CoalescingTransport(InstanceId instance) : IMirrorPulseWorkerUploadTransport,
        IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        public byte[]? Bytes { get; private set; }
        public List<Guid> UploadedOperations { get; } = [];
        public bool RejectFirstUpload { get; set; }
        private string? Revision => Bytes is null ? null : Convert.ToHexString(SHA256.HashData(Bytes));
        private void Require(InstanceId actual, string? rootKey) { Assert.AreEqual(instance, actual); Assert.AreEqual("docs", rootKey); }
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            Require(request.InstanceId, request.RootKey); Assert.AreEqual("coalesced.txt", request.NormalizedPath);
            if (RejectFirstUpload)
            {
                RejectFirstUpload = false; Bytes = "concurrent remote bytes"u8.ToArray();
                throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, Revision);
            }
            Assert.AreEqual(request.ExpectedRevision, Revision); Assert.IsNotNull(request.OperationId);
            using var output = new MemoryStream(); await request.Content.CopyToAsync(output, token); Bytes = output.ToArray();
            Assert.AreEqual(request.Length, Bytes.LongLength); Assert.AreEqual(request.ExpectedContentSha256, Revision);
            UploadedOperations.Add(request.OperationId.Value); return Revision!;
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey); return ValueTask.FromResult(Revision); }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(Bytes is null ? [] :
                [new("coalescing:file", Revision!, "File", "coalesced.txt", Bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey); return ValueTask.FromResult<Stream>(new MemoryStream(Bytes!.AsSpan((int)request.Offset, (int)request.Length).ToArray())); }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token) => throw new AssertFailedException("No delete is part of this content chain.");
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) => throw new AssertFailedException("No move is part of this content chain.");
    }
}
