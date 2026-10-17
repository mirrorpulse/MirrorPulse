using System.Diagnostics;
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

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpAtomicallyTransfersNeverStartedIntentAndRecoversAllOriginalAcknowledgements() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: false, prepareOriginal: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpKeepsLocalAbsenceByConditionalDeletionAndRecoversTheWholeGroup() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: true, verificationEffect: MirrorPulseCoalescedEffect.VerifyRemoteAbsence);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpKeepsResidentContentAfterARoundTripRenameAndRecoversTheWholeGroup() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: true, verificationEffect: MirrorPulseCoalescedEffect.VerifyRemoteUnchanged);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpVerifiesUnchangedResidentBytesWithoutAnotherUploadAndRecoversAllOriginals() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: false, verificationEffect: MirrorPulseCoalescedEffect.VerifyRemoteUnchanged);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpKeepsLocalMoveContentAndRecoversBothEffectsBeforeAcknowledgingOriginals() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: true, verificationEffect: MirrorPulseCoalescedEffect.MoveFile);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [SupportedOSPlatform("windows10.0.26100")]
    public Task NativeProductionPumpMovesProvenResidentContentWithoutUploadingAndConfirmsItsActualRevision() =>
        RunProductionCoalescingScenarioAsync(resolveConflict: false, verificationEffect: MirrorPulseCoalescedEffect.MoveFile);

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RunProductionCoalescingScenarioAsync(bool resolveConflict, bool prepareOriginal = false,
        MirrorPulseCoalescedEffect? verificationEffect = null)
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
        bool absence = verificationEffect == MirrorPulseCoalescedEffect.VerifyRemoteAbsence;
        bool unchanged = verificationEffect == MirrorPulseCoalescedEffect.VerifyRemoteUnchanged;
        bool moveOnly = verificationEffect == MirrorPulseCoalescedEffect.MoveFile;
        bool hasSeed = unchanged || moveOnly;
        var remote = new CoalescingTransport(instance)
        {
            RejectFirstUpload = resolveConflict && verificationEffect is null,
            AllowDelete = absence,
            AllowMove = moveOnly
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid planId = Guid.Empty; Guid[] originals = []; Guid effectId = Guid.Empty;
        Guid? retryCommandId = null;
        MirrorPulseMutationIntent? transferredOriginal = null;
        Guid? seedPlanId = null;
        MirrorPulseContentAcceptanceProof? seedAcceptance = null;
        MirrorPulseContentConfirmationReceipt? seedReceipt = null;
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
                string file = Path.Combine(paths.SyncRootPath, "Docs", owner > 0 && moveOnly ? "moved.txt" : "coalesced.txt");
                if (owner == 0)
                {
                    (await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token)).ThrowIfAnyFailed();
                    await File.WriteAllTextAsync(file, "first local edit", timeout.Token);
                    var source = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    MirrorPulseJournalUploadBatch window;
                    while (!(window = await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.RelativePath == "coalesced.txt"))
                        await Task.Delay(25, timeout.Token);
                    Guid first = window.ReadyCommands.First(command => command.RelativePath == "coalesced.txt").OperationId;
                    if (prepareOriginal)
                    {
                        var command = window.ReadyCommands.Single(candidate => candidate.OperationId == first);
                        Assert.AreEqual(MirrorPulseWorkerChangeKind.Create, command.Kind);
                        var cloudFile = fileSystem.GetFile("Docs/coalesced.txt");
                        var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router).CaptureUploadBindingAsync(
                            cloudFile, await cloudFile.InspectAsync(timeout.Token), timeout.Token);
                        byte[] initial = await File.ReadAllBytesAsync(file, timeout.Token);
                        transferredOriginal = new(first, instance, "docs", command.Kind, command.RelativePath, command.PreviousRelativePath,
                            false, null, initial.Length, Convert.ToHexString(SHA256.HashData(initial)), MirrorPulseMutationOrigin.Journal, binding);
                        var prepared = await catalog.PrepareMutationAsync(transferredOriginal, timeout.Token);
                        Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, prepared.ExecutionEvidence);
                    }
                    await File.WriteAllTextAsync(file, "final coalesced bytes", timeout.Token);
                    while ((window = await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Count(command => command.RelativePath == "coalesced.txt") < 2)
                        await Task.Delay(25, timeout.Token);
                    Assert.IsNotNull(window.CompleteWindow); Assert.Contains(first, window.ReadyCommands.Select(command => command.OperationId).ToArray());
                    if (absence)
                    {
                        File.Delete(file);
                        while (!(window = await source.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.Kind == MirrorPulseWorkerChangeKind.Delete))
                            await Task.Delay(25, timeout.Token);
                        remote.Seed("concurrent remote bytes"u8.ToArray());
                    }
                    if (hasSeed)
                    {
                        await feed.DisposeAsync();
                        await using (var seedFeed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 }))
                        {
                            var seedCompletion = new MirrorPulseJournalUploadCompletion(seedFeed, state,
                                new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
                            await using var seedPump = new MirrorPulseJournalUploadPump(seedFeed, router, catalog, remote, remote, state,
                                paths.SyncRootPath, paths.DataRootPath, _ => true, seedCompletion, mutations: remote, ranges: remote,
                                directories: remote, fileSystem: fileSystem);
                            await seedPump.StartAsync(timeout.Token);
                            long? quietSince = null;
                            while (true)
                            {
                                if (remote.UploadedOperations.Count == 1)
                                {
                                    var seeded = (await catalog.ReadJournalCoalescingPlansAsync(timeout.Token)).Single();
                                    var acceptedSnapshot = await fileSystem.GetFile("Docs/coalesced.txt").InspectAsync(timeout.Token);
                                    await using var checking = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                                    bool pending = false;
                                    foreach (var member in seeded.Members)
                                        pending |= await checking.Operations.GetAsync(member.OperationId, timeout.Token) is not null;
                                    pending |= (await checking.Operations.ListByItemIdAsync(seeded.ItemId, 1, timeout.Token)).Count != 0;
                                    if (acceptedSnapshot.ItemId is { } acceptedItemId && acceptedItemId != seeded.ItemId)
                                        pending |= (await checking.Operations.ListByItemIdAsync(acceptedItemId, 1, timeout.Token)).Count != 0;
                                    await checking.RollbackAsync(timeout.Token);
                                    if (pending) quietSince = null;
                                    else
                                    {
                                        quietSince ??= Stopwatch.GetTimestamp();
                                        if (Stopwatch.GetElapsedTime(quietSince.Value) >= TimeSpan.FromSeconds(1))
                                        { seedPlanId = seeded.PlanId; break; }
                                    }
                                }
                                await Task.Delay(25, timeout.Token);
                            }
                        }
                        Assert.IsTrue((await fileSystem.GetFile("Docs/coalesced.txt").InspectAsync(timeout.Token)).IsPlaceholder);
                        Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(remote.UploadedOperations[0], timeout.Token))!.MayAcknowledge);
                        seedAcceptance = await catalog.ReadContentAcceptanceProofAsync(remote.UploadedOperations[0], timeout.Token);
                        seedReceipt = await catalog.ReadContentConfirmationReceiptAsync(remote.UploadedOperations[0], timeout.Token);
                        Assert.IsNotNull(seedAcceptance); Assert.IsNotNull(seedReceipt);
                        await using var moves = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
                        await moves.StartAsync(timeout.Token);
                        var moveSource = new MirrorPulseJournalUploadSource(moves, router, catalog, _ => true);
                        string middle = Path.Combine(paths.SyncRootPath, "Docs", "middle.txt");
                        File.Move(file, middle);
                        while (!(window = await moveSource.ReadPendingAsync(timeout.Token)).ReadyCommands.Any(command => command.Kind == MirrorPulseWorkerChangeKind.Move))
                            await Task.Delay(25, timeout.Token);
                        if (moveOnly) file = Path.Combine(paths.SyncRootPath, "Docs", "moved.txt");
                        File.Move(middle, file);
                        while ((window = await moveSource.ReadPendingAsync(timeout.Token)).ReadyCommands.Count(command => command.Kind == MirrorPulseWorkerChangeKind.Move) < 2)
                            await Task.Delay(25, timeout.Token);
                        TestContext.WriteLine((moveOnly ? "MoveContentOriginalWindow: " : "RoundTripOriginalWindow: ") + string.Join(",", window.CompleteWindow!.Select(command =>
                            $"sequence={command.Sequence};kind={command.Kind};sameItem={command.ItemId == window.ReadyCommands[0].ItemId}")));
                        if (resolveConflict) remote.Seed("concurrent remote bytes"u8.ToArray());
                    }
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
                            new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), paths, router, remote, center, scheduler, fileSystem);
                        var decision = await actions.ApplyAsync(conflicts[1].ConflictId, MirrorPulseConflictAction.KeepLocal, timeout.Token);
                        retryCommandId = decision.ResolutionId;
                        if (moveOnly)
                        {
                            var ownerPlan = (await catalog.ReadJournalCoalescingOwnerAsync(Guid.Parse(conflicts[1].ChangeId), timeout.Token))!;
                            var originalProgram = (await catalog.ReadJournalCoalescingExecutionAsync(ownerPlan.PlanId, timeout.Token))!;
                            Assert.IsTrue(originalProgram.IsMoveOnly,
                                "The complete original window must produce a proven move-only execution before KeepLocal adds content.");
                            if (ownerPlan.Effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile)
                            {
                                Assert.AreEqual(3, originalProgram.Version); Assert.IsNotNull(originalProgram.ObservedContent);
                                Assert.AreEqual(seedAcceptance!.OperationId, originalProgram.ObservedContent.AcceptanceOperationId);
                            }
                            else Assert.AreEqual(MirrorPulseCoalescedEffect.MoveFile, ownerPlan.Effect);
                            var dispatch = await catalog.ReadJournalCoalescingDispatchAsync(ownerPlan.PlanId, timeout.Token);
                            Assert.IsTrue(dispatch.HasMoveContent); Assert.HasCount(2, dispatch.Steps);
                        }
                        Assert.IsEmpty(center.Query()); Assert.IsEmpty(await catalog.ReadUploadConflictsAsync(cancellationToken: timeout.Token));
                    }
                    await reached.Task.WaitAsync(timeout.Token);
                    var plan = (await catalog.ReadJournalCoalescingPlansAsync(timeout.Token)).Single(candidate => candidate.PlanId != seedPlanId);
                    var execution = (await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, timeout.Token))!;
                    if (unchanged)
                    {
                        // Keep the real notification plan. Modified notifications
                        // remain updates; the frozen execution proves net content.
                        Assert.IsTrue(plan.Effect is MirrorPulseCoalescedEffect.UpdateFile or MirrorPulseCoalescedEffect.VerifyRemoteUnchanged);
                        Assert.AreEqual(MirrorPulseCoalescingStepKind.VerifyUnchanged, execution.Steps[0].Kind);
                        if (plan.Effect == MirrorPulseCoalescedEffect.UpdateFile)
                        {
                            Assert.IsNotNull(execution.ObservedContent);
                            Assert.AreEqual(seedAcceptance!.OperationId, execution.ObservedContent.AcceptanceOperationId);
                            Assert.AreEqual(seedAcceptance.Sha256, execution.ObservedContent.Content.Sha256);
                            Assert.AreEqual(seedAcceptance.UploadBinding.LocalObject, execution.ObservedContent.Content.UploadBinding.LocalObject);
                        }
                        Assert.AreEqual(seedAcceptance, await catalog.ReadContentAcceptanceProofAsync(seedAcceptance!.OperationId, timeout.Token));
                        Assert.AreEqual(seedReceipt, await catalog.ReadContentConfirmationReceiptAsync(seedAcceptance.OperationId, timeout.Token));
                    }
                    else if (moveOnly)
                    {
                        Assert.IsTrue(execution.IsMoveOnly);
                        if (plan.Effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile)
                        {
                            Assert.AreEqual(3, execution.Version); Assert.IsNotNull(execution.ObservedContent);
                            Assert.AreEqual(seedAcceptance!.OperationId, execution.ObservedContent.AcceptanceOperationId);
                            Assert.AreEqual(seedAcceptance.Sha256, execution.ObservedContent.Content.Sha256);
                        }
                        else Assert.AreEqual(MirrorPulseCoalescedEffect.MoveFile, plan.Effect);
                    }
                    else Assert.AreEqual(verificationEffect ?? MirrorPulseCoalescedEffect.CreateFile, plan.Effect);
                    Assert.HasCount(1, execution.Steps);
                    var current = await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0, timeout.Token);
                    if (verificationEffect is not null)
                    {
                        Assert.IsNull(execution.Content);
                        string? expectedBaseline = absence ? null : Convert.ToHexString(SHA256.HashData("final coalesced bytes"u8));
                        Assert.AreEqual(expectedBaseline, execution.ExpectedRevision);
                        string expectedRemoteRevision = Convert.ToHexString(SHA256.HashData(resolveConflict ? "concurrent remote bytes"u8 : "final coalesced bytes"u8));
                        Assert.AreEqual(expectedRemoteRevision, current.Intent.ExpectedRevision);
                        Assert.AreEqual(moveOnly ? MirrorPulseWorkerChangeKind.Move : absence ? MirrorPulseWorkerChangeKind.Delete :
                            resolveConflict ? MirrorPulseWorkerChangeKind.ContentUpdate : MirrorPulseWorkerChangeKind.MetadataUpdate, current.Intent.Kind);
                    }
                    if (prepareOriginal)
                    {
                        Assert.IsNotNull(transferredOriginal);
                        Assert.HasCount(1, execution.OriginalMutations);
                        Assert.AreEqual(transferredOriginal.OperationId, execution.OriginalMutations[0].OperationId);
                        var original = (await catalog.ReadMutationAsync(transferredOriginal.OperationId, timeout.Token))!;
                        Assert.AreEqual(transferredOriginal, original.Intent);
                        Assert.AreEqual(MirrorPulseMutationState.Superseded, original.State);
                        Assert.AreEqual(MirrorPulseMutationExecutionEvidence.NeverStarted, original.ExecutionEvidence);
                        Assert.AreEqual(plan.PlanId, original.SupersededByPlanId);
                        Assert.AreNotEqual(transferredOriginal.ContentSha256, execution.Content!.Sha256);
                        Assert.AreEqual(transferredOriginal.UploadBinding!.LocalObject, execution.Content.UploadBinding.LocalObject);
                    }
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
                    Assert.IsFalse(originals.Contains(effectId));
                    if (absence)
                    {
                        Assert.IsEmpty(remote.UploadedOperations); Assert.HasCount(1, remote.DeletedOperations);
                        Assert.AreEqual(effectId, remote.DeletedOperations[0]); Assert.IsNull(remote.Bytes); Assert.IsFalse(File.Exists(file));
                        Assert.IsNull(await catalog.ReadContentConfirmationReceiptAsync(effectId, timeout.Token));
                    }
                    else
                    {
                        Assert.HasCount(hasSeed ? resolveConflict ? 2 : 1 : 1, remote.UploadedOperations);
                        if (moveOnly)
                        {
                            Assert.HasCount(1, remote.MovedOperations); Assert.AreEqual(effectId, remote.MovedOperations[0]);
                            var dispatch = await catalog.ReadJournalCoalescingDispatchAsync(plan.PlanId, timeout.Token);
                            if (resolveConflict)
                            {
                                Assert.IsTrue(dispatch.HasMoveContent); Assert.HasCount(2, dispatch.Steps);
                                var upload = await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 1, timeout.Token);
                                Assert.AreEqual(MirrorPulseMutationState.Acknowledged, upload.State);
                                string? uploadBaseline = upload.Intent.ExpectedRevision;
                                Assert.AreEqual("moved/" + Convert.ToHexString(SHA256.HashData("concurrent remote bytes"u8)), uploadBaseline);
                                Assert.AreEqual(upload.Intent.OperationId, remote.UploadedOperations[^1]);
                                Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(upload.Intent.OperationId, timeout.Token))!.MayAcknowledge);
                                Assert.AreNotEqual(effectId, upload.Intent.OperationId);
                                Assert.IsFalse(originals.Contains(upload.Intent.OperationId));
                            }
                            else
                            {
                                Assert.IsFalse(dispatch.HasMoveContent); Assert.HasCount(1, dispatch.Steps);
                                Assert.IsNotNull(execution.ObservedContent);
                                var moveProof = (await catalog.ReadContentAcceptanceProofAsync(effectId, timeout.Token))!;
                                Assert.AreEqual(current.AcceptedRevision, moveProof.AcceptedRevision);
                                Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(effectId, timeout.Token))!.MayAcknowledge);
                                var actual = await fileSystem.GetFile("Docs/moved.txt").InspectAsync(timeout.Token);
                                Assert.AreEqual(CloudSynchronizationState.InSync, actual.SynchronizationState);
                                Assert.AreEqual(current.AcceptedRevision, CloudPlaceholderIdentity.Decode(actual.PlaceholderIdentity.Span).RemoteRevision);
                            }
                            Assert.AreEqual(seedAcceptance, await catalog.ReadContentAcceptanceProofAsync(seedAcceptance!.OperationId, timeout.Token));
                            Assert.AreEqual(seedReceipt, await catalog.ReadContentConfirmationReceiptAsync(seedAcceptance.OperationId, timeout.Token));
                        }
                        else if (!unchanged || resolveConflict) Assert.AreEqual(effectId, remote.UploadedOperations[^1]);
                        else Assert.AreNotEqual(effectId, remote.UploadedOperations[0]);
                        Assert.AreEqual("final coalesced bytes", System.Text.Encoding.UTF8.GetString(remote.Bytes!));
                        if (!moveOnly && (!unchanged || resolveConflict)) Assert.IsTrue((await catalog.ReadContentConfirmationReceiptAsync(effectId, timeout.Token))!.MayAcknowledge);
                        else if (!moveOnly)
                        {
                            Assert.IsNull(await catalog.ReadContentAcceptanceProofAsync(effectId, timeout.Token));
                            Assert.IsNull(await catalog.ReadContentConfirmationReceiptAsync(effectId, timeout.Token));
                            Assert.AreEqual(CloudSynchronizationState.InSync, (await fileSystem.GetFile("Docs/coalesced.txt").InspectAsync(timeout.Token)).SynchronizationState);
                        }
                    }
                    Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await catalog.ReadMutationAsync(effectId, timeout.Token))!.State);
                    await using var transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                    foreach (Guid id in originals)
                        Assert.AreEqual(owner == 0, await transaction.Operations.GetAsync(id, timeout.Token) is not null);
                    await transaction.RollbackAsync(timeout.Token);
                    TestContext.WriteLine($"ProductionCoalescing: owner={owner}; originalCount={originals.Length}; separateEffectId=True; " +
                        $"remoteUploads={(absence || unchanged && !resolveConflict ? 0 : 1)}; initialSeedUploads={(hasSeed ? 1 : 0)}; conditionalDeletes={(absence ? 1 : 0)}; conditionalMoves={remote.MovedOperations.Count}; " +
                        $"actualNativeConfirmation={!absence}; officialProjection=True; originalIdsRetainedAtCut=True; " +
                        $"reopenedOwnerDoesNotRedispatch=True; wholeOriginalGroupAcknowledged=True; authorizedGroupRetry={resolveConflict}; " +
                        $"explicitNeverStartedTransfer={prepareOriginal}; verificationDecision={verificationEffect}; rawPlanEffect={plan.Effect}; " +
                        $"actualProgram={execution.Steps[0].Kind}; observedContentProof={execution.ObservedContent is not null}; retainedSeedAcceptanceUnchanged={hasSeed}; moveContentDecision={moveOnly}; strictHostIntegration=False.");
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
        public List<Guid> DeletedOperations { get; } = [];
        public List<Guid> MovedOperations { get; } = [];
        public bool AllowDelete { get; init; }
        public bool AllowMove { get; init; }
        private string _path = "coalesced.txt";
        private string? _movedRevision;
        public void Seed(byte[] bytes) { Bytes = bytes; _movedRevision = null; }
        public bool RejectFirstUpload { get; set; }
        private string? Revision => Bytes is null ? null : _movedRevision ?? Convert.ToHexString(SHA256.HashData(Bytes));
        private void Require(InstanceId actual, string? rootKey) { Assert.AreEqual(instance, actual); Assert.AreEqual("docs", rootKey); }
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            Require(request.InstanceId, request.RootKey); Assert.AreEqual(_path, request.NormalizedPath);
            if (RejectFirstUpload)
            {
                RejectFirstUpload = false; Bytes = "concurrent remote bytes"u8.ToArray();
                throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, Revision);
            }
            Assert.AreEqual(request.ExpectedRevision, Revision); Assert.IsNotNull(request.OperationId);
            using var output = new MemoryStream(); await request.Content.CopyToAsync(output, token); Bytes = output.ToArray();
            _movedRevision = null;
            Assert.AreEqual(request.Length, Bytes.LongLength); Assert.AreEqual(request.ExpectedContentSha256, Revision);
            UploadedOperations.Add(request.OperationId.Value); return Revision!;
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey); return ValueTask.FromResult(request.NormalizedPath == _path ? Revision : null); }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(Bytes is null ? [] :
                [new("coalescing:file", Revision!, "File", _path, Bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey); return ValueTask.FromResult<Stream>(new MemoryStream(Bytes!.AsSpan((int)request.Offset, (int)request.Length).ToArray())); }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        {
            if (!AllowDelete) throw new AssertFailedException("No delete is part of this content chain.");
            token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey);
            Assert.AreEqual("coalesced.txt", request.NormalizedPath); Assert.AreEqual(request.ExpectedRevision, Revision);
            Assert.IsNotNull(request.OperationId); Bytes = null; DeletedOperations.Add(request.OperationId.Value);
            return ValueTask.FromResult<string?>(null);
        }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token)
        {
            if (!AllowMove) throw new AssertFailedException("No move is part of this content chain.");
            token.ThrowIfCancellationRequested(); Require(request.InstanceId, request.RootKey);
            Assert.AreEqual("docs", request.DestinationRootKey); Assert.AreEqual("coalesced.txt", request.SourcePath);
            Assert.AreEqual("moved.txt", request.DestinationPath); Assert.IsNotNull(request.OperationId);
            if (request.ExpectedRevision != Revision) throw new MirrorPulseWorkerMutationConflictException(request.ExpectedRevision, Revision);
            Assert.AreEqual(request.SourcePath, _path); Assert.IsNotNull(Bytes);
            _path = request.DestinationPath; _movedRevision = "moved/" + Revision;
            MovedOperations.Add(request.OperationId.Value); return ValueTask.FromResult(Revision!);
        }
    }
}
