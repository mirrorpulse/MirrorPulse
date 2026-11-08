using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("seal")]
    [DataRow("writer")]
    [DataRow("unowned-seal")]
    public Task NativeKeepRemoteFirstSealRequiresTheOriginalFileAndJournalAndReplaysWithoutWriting(string scenario) =>
        RunKeepRemoteResidentFixtureAsync(scenario, 3);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow(65537)]
    [DataRow(3)]
    [DataRow(0)]
    public Task NativeKeepRemoteResidentByteCopyRetainsPlaceholderAndOriginalJournal(int selectedLength) =>
        RunKeepRemoteResidentFixtureAsync("copy", selectedLength);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("AfterWriteStart")]
    [DataRow("AfterContentFlush")]
    [DataRow("AfterContentRecorded")]
    public Task NativeKeepRemoteResidentWriterKeepsItsWriteAndCompletionFactsAcrossRetry(string checkpoint) =>
        RunKeepRemoteResidentFixtureAsync(checkpoint, 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteResidentWriterPreservesChangedLocalBytesBeforeItsFirstWrite() =>
        RunKeepRemoteResidentFixtureAsync("changed-local", 3);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("AfterNativeStart")]
    [DataRow("AfterSdkReturned")]
    [DataRow("AfterNativeRecorded")]
    [DataRow("native-changed")]
    public Task NativeKeepRemoteConfirmationRetainsOriginalAttemptsAndRecoversLostSdkReturns(string checkpoint) =>
        RunKeepRemoteResidentFixtureAsync(checkpoint, 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow("AfterRestoreStart")]
    [DataRow("AfterAccessApplied")]
    [DataRow("AfterScopeReturned")]
    [DataRow("AfterRestoreRecorded")]
    [DataRow("restore-later-edit")]
    public Task NativeKeepRemoteRestorationRetainsActualAccessResultsAndPreservesOrdinaryLaterEdits(string checkpoint) =>
        RunKeepRemoteResidentFixtureAsync(checkpoint, 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteRoleHandoffRecoversLostReadbackWithoutReapplyingContent() =>
        RunKeepRemoteResidentFixtureAsync("role-handoff", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteNewRoleResumesOriginalWriteAndConfirmsSelectedBytes() =>
        RunKeepRemoteResidentFixtureAsync("role-after-write", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteAcknowledgementRecoversLostSdkReturnAndPreservesLaterEdit() =>
        RunKeepRemoteResidentFixtureAsync("acknowledgement", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteDefaultCommandResumesFirstPermissionIntentBeforeRootAudit() =>
        RunKeepRemoteResidentFixtureAsync("host-command", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteIndependentRuntimeSettlesOriginalCommandBeforeReadmission() =>
        RunKeepRemoteResidentFixtureAsync("host-process", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteUploadPumpResumesAcceptedCommandAfterRootAudit() =>
        RunKeepRemoteResidentFixtureAsync("host-preparation", 65537);

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public Task NativeKeepRemoteCfSharpAccessOnlyPreservesOriginalJournalAndRealSameLengthWrite() =>
        RunKeepRemoteResidentFixtureAsync("access-journal-qualification", 65537);

    private async Task RunKeepRemoteResidentFixtureAsync(string scenario, int selectedLength)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.keep-remote"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using var caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var token = deadline.Token;
        await using var role = new MirrorPulseNamespaceExecutionSession();
        await using var actor = new MirrorPulseNamespaceActor();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        var provider = new ProtectedPermissionNoSourceProvider();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 8, 14]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
            await role.RunNamespaceOperationAsync(() => fileSystem.StartAsync(token).AsTask());
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            var file = fileSystem.GetFile("Docs/original.bin");
            Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
            await File.WriteAllBytesAsync(file.FullPath, [1, 2, 3], token);
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            if (scenario is "host-command" or "host-process" or "host-preparation" or "access-journal-qualification")
            {
                foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), file })
                    SetProtectedFixtureUserOwner(item, caller.User!);
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router,
                    role, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), token);
            }
            else
            {
                await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, token, waitForFixtureMetadata: true);
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, token);
                await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, token);
            }
            await using var feed = fileSystem.CreateLocalChangeFeed(new() { BatchSize = 1 });
            await feed.StartAsync(token);
            var journal = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
            Guid itemId = (await file.InspectAsync(token)).ItemId!.Value;
            await File.AppendAllTextAsync(file.FullPath, " first local edit", token);
            await ReadEditedWindowAsync(1);
            await File.AppendAllTextAsync(file.FullPath, " latest local edit", token);
            var window = await ReadEditedWindowAsync(2);
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window.CompleteWindow!, itemId, window.WindowUpperSequence, new HashSet<Guid>());
            Assert.IsNotNull(plan);
            Assert.AreEqual(MirrorPulseCoalescedEffect.UpdateFile, plan.Effect);
            Assert.HasCount(2, plan.Members);
            byte[] local = await File.ReadAllBytesAsync(file.FullPath, token);
            var snapshot = await file.InspectAsync(token);
            if (scenario is "access-journal-qualification")
            {
                await VerifyAccessOnlyJournalQualificationAsync(state, feed, router, file, role, plan, snapshot, local, token);
                Assert.AreEqual(0, provider.Reads);
                return;
            }
            Assert.IsNotNull(snapshot.RemoteId);
            var binding = await new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role)
                .CaptureUploadBindingAsync(file, snapshot, token);
            var content = new MirrorPulseCoalescingContent(local.Length, Convert.ToHexString(SHA256.HashData(local)), binding);
            await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window.CompleteWindow!, "prior-remote", content, token);
            var remote = new KeepBothRemote(Path.Combine(paths.DataRootPath, "keep-remote-selected.bin"), snapshot.RemoteId);
            byte[] selectedBytes = new byte[selectedLength];
            new Random(518).NextBytes(selectedBytes);
            remote.Seed(selectedBytes);
            await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() =>
                new MirrorPulseJournalCoalescingExecutor(catalog, remote, remote, remote, new(remote)).ExecuteAsync(plan.PlanId,
                    (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(local, writable: false)), token).AsTask());
            Guid conflictId = Guid.Empty;
            foreach (var member in plan.Members)
            {
                var conflict = new MirrorPulseConflictRecord(Guid.NewGuid(), root.InstanceId, member.OperationId.ToString("D"), "Docs/original.bin",
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "prior-remote", "selected-remote", DateTimeOffset.UtcNow);
                await catalog.SaveUploadConflictAsync(conflict, token);
                conflictId = conflict.ConflictId;
            }
            if (scenario is "host-preparation")
            {
                await VerifyKeepRemotePreparationRecoveryAsync(paths, catalog, router, root, state, feed, remote, fileSystem,
                    actor, scheduler, work, role, conflictId, plan, snapshot, local, selectedBytes, token);
                Assert.AreEqual(0, provider.Reads);
                return;
            }
            if (scenario is "host-command" or "host-process")
            {
                var firstOwner = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
                    paths, router, remote, scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor,
                    namespaceExecution: role, stats: remote, remoteRanges: remote, remoteDirectories: remote)
                {
                    KeepRemoteFileStarted = _ => ValueTask.FromException(new IOException("Synthetic interruption after first KeepRemote permission intent."))
                };
                await Assert.ThrowsExactlyAsync<IOException>(() => firstOwner.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token).AsTask());
                Guid retainedCommand = (await catalog.ReadConflictKeepRemoteOwnerAsync(plan.Members[0].OperationId, token))!.Value;
                var firstStart = await catalog.ReadConflictKeepRemoteFileStartAsync(retainedCommand, token);
                var firstAction = await catalog.ReadConflictKeepRemoteFileActionAsync(retainedCommand, token);
                Assert.IsNotNull(firstStart);
                Assert.IsNotNull(firstAction);
                Assert.AreEqual(firstStart.StartedAt, firstAction.PreparedAt);
                Assert.IsNull((await catalog.ReadConflictKeepRemoteFileContentAsync(retainedCommand, token))!.WriteStart);
                Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(file.FullPath, token));
                if (scenario == "host-process")
                {
                    await feed.DisposeAsync();
                    await fileSystem.DisposeAsync();
                    await catalog.DisposeAsync();
                    await role.DisposeAsync();
                    await VerifyIndependentKeepRemoteRuntimeAsync(directory, paths, firstStart, firstAction,
                        plan.Members.Count, selectedBytes, local, token);
                    return;
                }
                int readsBeforeRecovery = remote.SourceReads;
                remote.DenySourceReads = true;
                await using var nextRole = new MirrorPulseNamespaceExecutionSession();
                var nextOwner = new MirrorPulseUploadConflictActions(catalog, state, feed, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
                    paths, router, remote, scheduler: scheduler, fileSystem: fileSystem, namespaceActor: actor, namespaceExecution: nextRole,
                    namespaceWork: work, stats: remote, remoteRanges: remote, remoteDirectories: remote);
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.IsTrue(await RetryWithRescanAsync(() =>
                    MirrorPulseUploadConflictActions.RetryKeepRemoteProtectionLossAsync(() =>
                        scheduler.RunAsync(root.InstanceId, stop => nextOwner.RecoverKeepRemoteBeforeAuditAsync(root, stop), token))));
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(retainedCommand, token))!.State);
                var handoff = (await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(retainedCommand, token)).Single();
                Assert.IsNotNull(handoff.Result);
                Assert.AreEqual(firstAction.ActionId, handoff.Start.FirstActionId);
                Assert.AreEqual(firstAction, await catalog.ReadConflictKeepRemoteFileActionAsync(retainedCommand, token));
                var applied = await file.InspectAsync(token);
                Assert.AreEqual(snapshot.LocalBinding, applied.LocalBinding);
                Assert.AreEqual("selected-remote", applied.RemoteRevision);
                Assert.AreEqual(CloudSynchronizationState.InSync, applied.SynchronizationState);
                CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token));
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                var startupStages = new List<string>();
                var startup = await new MirrorPulseNamespaceStartupCoordinator(fileSystem, catalog, router, nextRole, actor, scheduler,
                    permissions, work, startupStages.Add).StartAsync(token);
                await using var startupGuard = startup.Guard;
                var audited = startup.Roots.Single();
                Assert.IsTrue(audited.Admitted, $"{audited.RecoveryCode} ({audited.NativeErrorCode}): {string.Join(", ", startupStages)}");
                Assert.IsTrue(work.IsAdmitted(root.RootId));
                var replayedCommand = await nextOwner.ApplyAsync(conflictId, MirrorPulseConflictAction.KeepRemote, token);
                Assert.AreEqual(retainedCommand, replayedCommand.ResolutionId);
                CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(replayedCommand.PreservedPath!, token));
                Assert.AreEqual(readsBeforeRecovery, remote.SourceReads);
                Assert.AreEqual(0, remote.AcceptedUploads);
                Assert.AreEqual(0, provider.Reads);
                return;
            }
            Guid commandId = Guid.NewGuid();
            await catalog.SaveUserCommandAsync(new(commandId, "keepremote", conflictId.ToString("D"), "pending"), token);
            var source = new MirrorPulseConflictRemoteSource(1, root.InstanceId.ToString(), root.UniquenessKey, "original.bin",
                snapshot.RemoteId, "selected-remote", selectedLength);
            await catalog.PrepareConflictKeepRemoteAsync(commandId, conflictId, itemId, "Docs/original.bin", source, content, token);
            var copyStore = new MirrorPulseConflictCopyStore(paths);
            await using var copies = await new MirrorPulseConflictKeepRemotePreserver(catalog, copyStore, new(copyStore, remote, remote, remote))
                .RetainAsync(commandId, (_, _) => ValueTask.FromResult<Stream>(File.OpenRead(file.FullPath)), token);
            int sourceReads = remote.SourceReads;
            Assert.IsGreaterThan(0, sourceReads);
            remote.DenySourceReads = true;
            var retainedJournal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
            await retainedJournal.RetainAsync(copies, token);
            var original = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(members.Values.Single(member => !member.IsDirectory).MemberId, token);
            Assert.IsNotNull(original);
            var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Context.Location.ParentMemberId!.Value, token);
            Assert.IsNotNull(parent);
            using var store = await state.OpenStoreFileLeaseAsync(token);
            await catalog.RetainConflictKeepRemoteFileStartAsync(commandId, store.Binding.VolumeSerialNumber, store.Binding.FileId,
                original, parent, DateTimeOffset.UtcNow, token);

            var seal = new MirrorPulseConflictKeepRemoteResidentSeal(fileSystem, catalog, state, feed, router, role, actor);
            await using var admission = await work.EnterPendingOperationAsync(root.RootId, token);
            if (scenario == "writer")
            {
                using var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                await Assert.ThrowsExactlyAsync<IOException>(() => ApplySealAsync().AsTask());
                Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
                return;
            }
            if (scenario == "unowned-seal")
            {
                await role.RunNamespaceOperationAsync(async () =>
                {
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(file, router, token);
                    await lease.ApplyDaclAsync(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(role), token);
                });
                try
                {
                    await ApplySealAsync();
                    Assert.Fail("A descriptor without an original permission action must be refused.");
                }
                catch (Exception failure) when (failure is InvalidDataException or IOException)
                {
                    Assert.IsNull(await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
                }
                return;
            }

            CloudProtectedLocalOperationResult first;
            try
            {
                first = await ApplySealAsync();
            }
            catch
            {
                await using var inspection = await state.OpenStore.BeginTransactionAsync(CancellationToken.None);
                var rows = await inspection.Operations.ListByItemIdAsync(itemId, 64, CancellationToken.None);
                foreach (var row in rows)
                    TestContext.WriteLine($"KeepRemoteSealJournal original={plan.Members.Any(member => member.OperationId == row.OperationId)} kind={row.Kind} sequence={row.Sequence} payloadLength={row.Payload.Length}.");
                await inspection.RollbackAsync(CancellationToken.None);
                throw;
            }
            Assert.IsTrue(first.CallbackCompleted && first.Drained && first.AccessDescriptorApplied && first.AccessDescriptorReadBack);
            var action = await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token);
            Assert.IsNotNull(action);
            Assert.AreEqual(original.Context.Member.LocalObject, action.LocalObject);
            Assert.AreEqual(original.ExpectedDacl, action.RestoreDacl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, token));
            var replay = await ApplySealAsync();
            Assert.IsTrue(replay.CallbackCompleted && replay.Drained);
            Assert.IsFalse(replay.AccessDescriptorApplied);
            Assert.AreEqual(action, await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
            await role.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(local, await File.ReadAllBytesAsync(file.FullPath, token)));
            if (scenario != "seal")
            {
                MirrorPulseKeepRemoteContentCheckpoint? interruption = Enum.TryParse<MirrorPulseKeepRemoteContentCheckpoint>(scenario, out var point)
                    ? point : null;
                if (scenario == "role-after-write")
                    interruption = MirrorPulseKeepRemoteContentCheckpoint.AfterContentFlush;
                bool interrupted = false;
                var writer = new MirrorPulseConflictKeepRemoteResidentWriter(fileSystem, catalog, state, feed, router, role, actor)
                {
                    Checkpoint = (current, _) =>
                    {
                        if (!interrupted && current == interruption)
                        {
                            interrupted = true;
                            return ValueTask.FromException(new IOException("Synthetic KeepRemote content interruption."));
                        }
                        return ValueTask.CompletedTask;
                    }
                };
                ValueTask<MirrorPulseConflictKeepRemoteFileContentCompletion> CopyAsync() =>
                    RunPhaseAsync(stop => writer.CopyAsync(root.RootId, copies, stop));
                if (scenario == "changed-local")
                {
                    byte[] latest = "a later local edit that must survive"u8.ToArray();
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        await using var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                        await target.WriteAsync(latest, token);
                        target.SetLength(latest.Length);
                        await target.FlushAsync(token);
                    });
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CopyAsync().AsTask());
                    Assert.IsNull((await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))!.WriteStart);
                    await role.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(latest, await File.ReadAllBytesAsync(file.FullPath, token)));
                    return;
                }
                if (interruption is not null)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => CopyAsync().AsTask());
                    var pending = await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token);
                    Assert.IsNotNull(pending?.WriteStart);
                    Assert.AreEqual(interruption == MirrorPulseKeepRemoteContentCheckpoint.AfterContentRecorded, pending.Completion is not null);
                }
                await using var resumedRole = scenario == "role-after-write" ? new MirrorPulseNamespaceExecutionSession() : null;
                var currentRole = resumedRole ?? role;
                MirrorPulseConflictKeepRemoteRoleResult? writeHandoff = null;
                MirrorPulseConflictKeepRemoteFileWriteStart? interruptedWrite = null;
                if (resumedRole is not null)
                {
                    interruptedWrite = (await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))!.WriteStart;
                    Assert.IsNotNull(interruptedWrite);
                    var recovery = new MirrorPulseConflictKeepRemoteResidentRoleRecovery(fileSystem, catalog, state, feed, router, resumedRole, actor);
                    writeHandoff = await RunPhaseAsync(stop => recovery.RecoverAsync(root.RootId, copies, stop));
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CopyAsync().AsTask());
                    writer = new(fileSystem, catalog, state, feed, router, resumedRole, actor);
                }
                var completion = await CopyAsync();
                Assert.AreEqual(copies.Remote.Proof, completion.Copy);
                Assert.IsNotNull((await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))!.WriteStart);
                var copied = await file.InspectAsync(token);
                Assert.IsTrue(copied.IsPlaceholder);
                Assert.AreEqual(snapshot.LocalBinding, copied.LocalBinding);
                CollectionAssert.AreEqual(snapshot.PlaceholderIdentity.ToArray(), copied.PlaceholderIdentity.ToArray());
                await currentRole.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token)));
                Assert.IsTrue(copies.Local!.Data.CanRead);
                MirrorPulseKeepRemoteNativeCheckpoint? nativeInterruption = Enum.TryParse<MirrorPulseKeepRemoteNativeCheckpoint>(scenario, out var nativePoint)
                    ? nativePoint : null;
                bool nativeInterrupted = false;
                var confirmation = new MirrorPulseConflictKeepRemoteResidentConfirmation(fileSystem, catalog, state, feed, router, currentRole, actor)
                {
                    Checkpoint = async (current, stop) =>
                    {
                        if (nativeInterrupted || current != nativeInterruption) return;
                        if (current == MirrorPulseKeepRemoteNativeCheckpoint.AfterSdkReturned)
                        {
                            // Observe actual progress at the lost-return cut, before a retry.
                            var atReturn = await file.InspectAsync(stop);
                            Assert.AreEqual(snapshot.LocalBinding, atReturn.LocalBinding);
                            // Allow a returned refusal to be recorded and recovered before interrupting a successful return.
                            if (atReturn.SynchronizationState != CloudSynchronizationState.InSync) return;
                            CollectionAssert.AreEqual(new CloudPlaceholderIdentity(itemId, source.RemoteId, source.RemoteRevision).Encode(),
                                atReturn.PlaceholderIdentity.ToArray());
                            await using var projection = await state.OpenStore.BeginTransactionAsync(stop);
                            var projected = await projection.Items.GetByItemIdAsync(itemId, stop);
                            Assert.IsNotNull(projected);
                            Assert.AreEqual(source.RemoteId, projected.RemoteId);
                            Assert.AreEqual(source.RemoteRevision, projected.RemoteRevision);
                            foreach (var member in plan.Members) Assert.IsNotNull(await projection.Operations.GetAsync(member.OperationId, stop));
                            await projection.RollbackAsync(stop);
                            Assert.IsNull((await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, stop))[^1].Result);
                        }
                        if (current == MirrorPulseKeepRemoteNativeCheckpoint.AfterNativeRecorded)
                        {
                            var recorded = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, stop);
                            if (recorded[^1].Result?.Receipt.MayAcknowledge != true) return;
                        }
                        nativeInterrupted = true;
                        throw new IOException("Synthetic KeepRemote native interruption.");
                    }
                };
                ValueTask<MirrorPulseConflictKeepRemoteNativeResult> ConfirmAsync() =>
                    RunPhaseAsync(stop => confirmation.ConfirmAsync(root.RootId, copies, stop));
                if (nativeInterruption is not null)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => ConfirmWithRecoveryAsync());
                    Assert.IsTrue(nativeInterrupted);
                    var pending = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token);
                    Assert.IsTrue(pending.Count is 1 or 2);
                    if (pending.Count == 2)
                    {
                        Assert.AreNotEqual(MirrorPulseKeepRemoteNativeCheckpoint.AfterNativeStart, nativeInterruption);
                        Assert.AreEqual(MirrorPulseContentConfirmationOutcome.ProtectionLost, pending[0].Result!.Receipt.Outcome);
                    }
                    if (nativeInterruption == MirrorPulseKeepRemoteNativeCheckpoint.AfterNativeRecorded)
                        Assert.IsTrue(pending[^1].Result?.Receipt.MayAcknowledge);
                    else
                        Assert.IsNull(pending[^1].Result);
                }
                if (scenario == "native-changed")
                {
                    byte[] latest = "a later edit after complete remote copying"u8.ToArray();
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        await using var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                        await target.WriteAsync(latest, token);
                        target.SetLength(latest.Length);
                        await target.FlushAsync(token);
                    });
                    var refused = await ConfirmAsync();
                    Assert.AreEqual(MirrorPulseContentConfirmationOutcome.ContentMismatch, refused.Receipt.Outcome);
                    Assert.IsFalse(refused.Receipt.MayAcknowledge);
                    Assert.AreEqual(refused, (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token)).Single().Result);
                    CollectionAssert.AreEqual(snapshot.PlaceholderIdentity.ToArray(), (await file.InspectAsync(token)).PlaceholderIdentity.ToArray());
                    Assert.AreEqual(completion, await CopyAsync());
                    await role.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(latest, await File.ReadAllBytesAsync(file.FullPath, token)));
                    return;
                }
                var (confirmed, confirmedAttempts) = await ConfirmWithRecoveryAsync();
                var selected = await file.InspectAsync(token);
                Assert.AreEqual(source.RemoteRevision, selected.RemoteRevision);
                Assert.AreEqual(snapshot.LocalBinding, selected.LocalBinding);
                await currentRole.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token)));
                if (writeHandoff is not null)
                {
                    Assert.AreEqual(interruptedWrite, (await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))!.WriteStart);
                    Assert.AreEqual(writeHandoff.RecoveryId, completion.RoleRecoveryId);
                    Assert.IsTrue(confirmedAttempts.All(attempt => attempt.Start.RoleRecoveryId == writeHandoff.RecoveryId));
                    Assert.AreEqual(action.ActionId, confirmed.ActionId);
                    Assert.AreEqual(CloudSynchronizationState.InSync, selected.SynchronizationState);
                    var restorer = new MirrorPulseConflictKeepRemoteResidentRestoration(fileSystem, catalog, state, feed, router, currentRole, actor);
                    var restored = await RunPhaseAsync(stop => restorer.RestoreAsync(root.RootId, copies, stop));
                    Assert.IsTrue(restored.ProductVerified);
                    Assert.AreEqual(writeHandoff.RecoveryId, (await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token)).Single().Start.RoleRecoveryId);
                    CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token));
                }
                else if (Enum.TryParse<MirrorPulseKeepRemoteRestoreCheckpoint>(scenario, out _) || scenario is "restore-later-edit" or "acknowledgement")
                {
                    await VerifyRestorationAsync();
                    if (scenario == "acknowledgement") return;
                }
                else if (scenario == "role-handoff")
                {
                    await VerifyRoleHandoffAsync();
                }
                else
                {
                    byte[] laterBytes = "a later edit after content completion"u8.ToArray();
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        await using var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                        await target.WriteAsync(laterBytes, token);
                        target.SetLength(laterBytes.Length);
                        await target.FlushAsync(token);
                    });
                    Assert.AreEqual(completion, await CopyAsync());
                    Assert.AreEqual(confirmed, await ConfirmAsync());
                    Assert.HasCount(confirmedAttempts.Count, await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token));
                    await role.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(laterBytes, await File.ReadAllBytesAsync(file.FullPath, token)));
                }

                async Task VerifyRoleHandoffAsync()
                {
                    var firstAction = (await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token))!;
                    MirrorPulseConflictKeepRemoteRoleStart lost;
                    await using (var secondRole = new MirrorPulseNamespaceExecutionSession())
                    {
                        var handoff = new MirrorPulseConflictKeepRemoteResidentRoleRecovery(fileSystem, catalog, state, feed, router, secondRole, actor)
                        {
                            Checkpoint = (point, _) => point == MirrorPulseKeepRemoteRoleCheckpoint.AfterMetadataHandoff
                                ? ValueTask.FromException(new IOException("Synthetic interruption after the retained file's role metadata write."))
                                : ValueTask.CompletedTask
                        };
                        await Assert.ThrowsExactlyAsync<IOException>(() => RecoverAsync(handoff).AsTask());
                        var pending = (await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(commandId, token)).Single();
                        lost = pending.Start;
                        Assert.IsNull(pending.Result);
                        Assert.AreEqual(secondRole.RoleSid.Value, lost.NextAction.RoleSid);
                        await secondRole.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token)));
                    }

                    await using var thirdRole = new MirrorPulseNamespaceExecutionSession();
                    var recovery = new MirrorPulseConflictKeepRemoteResidentRoleRecovery(fileSystem, catalog, state, feed, router, thirdRole, actor);
                    var result = await RecoverAsync(recovery);
                    var history = await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(commandId, token);
                    Assert.HasCount(2, history);
                    Assert.AreEqual(lost, history[0].Start);
                    Assert.IsNull(history[0].Result);
                    Assert.AreEqual(lost.NextAction.ActionId, history[1].Start.PreviousRecoveryId);
                    Assert.AreEqual(thirdRole.RoleSid.Value, history[1].Start.NextAction.RoleSid);
                    Assert.AreEqual(result, history[1].Result);
                    Assert.AreEqual(result, await RecoverAsync(recovery));
                    Assert.AreEqual(firstAction, await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token));
                    Assert.AreEqual(completion, (await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))!.Completion);
                    CollectionAssert.AreEqual(confirmedAttempts.ToArray(), (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token)).ToArray());
                    Assert.HasCount(0, await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token));
                    var afterHandoff = await file.InspectAsync(token);
                    Assert.AreEqual(snapshot.LocalBinding, afterHandoff.LocalBinding);
                    Assert.AreEqual(CloudSynchronizationState.InSync, afterHandoff.SynchronizationState);
                    CollectionAssert.AreEqual(selected.PlaceholderIdentity.ToArray(), afterHandoff.PlaceholderIdentity.ToArray());
                    await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file.FullPath, token));
                    await thirdRole.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token)));

                    var oldRestore = new MirrorPulseConflictKeepRemoteResidentRestoration(fileSystem, catalog, state, feed, router, role, actor);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RestoreUnderAsync(oldRestore).AsTask());
                    Assert.HasCount(0, await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token));
                    var currentRestore = new MirrorPulseConflictKeepRemoteResidentRestoration(fileSystem, catalog, state, feed, router, thirdRole, actor);
                    var restored = await RestoreUnderAsync(currentRestore);
                    Assert.IsTrue(restored.ProductVerified);
                    var restoreAttempt = (await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token)).Single();
                    Assert.AreEqual(result.RecoveryId, restoreAttempt.Start.RoleRecoveryId);
                    Assert.AreEqual(firstAction.ActionId, restoreAttempt.Start.ActionId);
                    Assert.AreEqual(restored, await RestoreUnderAsync(currentRestore));
                    CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token));
                    CollectionAssert.AreEqual(confirmedAttempts.ToArray(), (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token)).ToArray());
                    Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));

                    ValueTask<MirrorPulseConflictKeepRemoteRoleResult> RecoverAsync(MirrorPulseConflictKeepRemoteResidentRoleRecovery worker) =>
                        RunPhaseAsync(stop => worker.RecoverAsync(root.RootId, copies, stop));

                    ValueTask<MirrorPulseConflictKeepRemoteRestoreResult> RestoreUnderAsync(MirrorPulseConflictKeepRemoteResidentRestoration worker) =>
                        RunPhaseAsync(stop => worker.RestoreAsync(root.RootId, copies, stop));
                }

                async Task<(MirrorPulseConflictKeepRemoteNativeResult Result, IReadOnlyList<MirrorPulseConflictKeepRemoteNativeAttempt> Attempts)> ConfirmWithRecoveryAsync()
                {
                    var beforeConfirmation = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token);
                    int expectedAttempts = beforeConfirmation.Count +
                        (beforeConfirmation.Any(attempt => attempt.Result?.Receipt.MayAcknowledge == true) ? 0 : 1);
                    var confirmed = await ConfirmAsync();
                    if (confirmed.Receipt.Outcome == MirrorPulseContentConfirmationOutcome.ProtectionLost)
                    {
                        // A returned refusal is not completion. A new scheduler admission must
                        // retain it and recheck the original authority before a fresh SDK hash pass.
                        // In particular, never continue a digest on the invalidated opaque owner.
                        var refused = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token);
                        Assert.HasCount(expectedAttempts, refused);
                        Assert.AreEqual(confirmed, refused[^1].Result);
                        Assert.IsFalse(confirmed.Receipt.MayAcknowledge);
                        Assert.IsTrue(confirmed.Receipt.NativeIdentityPrepared);
                        Assert.IsFalse(confirmed.Receipt.NativeApplied);
                        Assert.IsFalse(confirmed.Receipt.NativeConfirmationVerified);
                        Assert.IsTrue(confirmed.Receipt.DurableProjectionCommitted);
                        Assert.AreEqual(MirrorPulseContentConfirmationStage.Reference, confirmed.Receipt.NativeStage);
                        Assert.IsFalse(confirmed.Receipt.ObservedInSync);
                        Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
                        await store.RequireCurrentAsync(store.Binding, token);
                        await RetryWithRescanAsync(() => new ValueTask<MirrorPulseConflictKeepRemoteJournalBinding>(
                            retainedJournal.RevalidateRetainedOriginalsAsync(copies, token)));
                        var prepared = await file.InspectAsync(token);
                        Assert.AreEqual(snapshot.LocalBinding, prepared.LocalBinding);
                        Assert.AreEqual(CloudContentAvailability.FullyAvailable, prepared.ContentAvailability);
                        Assert.AreEqual(CloudSynchronizationState.NotInSync, prepared.SynchronizationState);
                        Assert.AreEqual(source.RemoteRevision, prepared.RemoteRevision);
                        Assert.AreEqual(Convert.ToBase64String(prepared.PlaceholderIdentity.Span), confirmed.Receipt.ObservedPlaceholderIdentity);
                        await currentRole.RunNamespaceOperationAsync(async () => CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token)));
                        TestContext.WriteLine("KeepRemote retained an actual ProtectionLost receipt; the next admission revalidates the original store, journal, object, seals and complete selected bytes.");
                        confirmed = await ConfirmAsync();
                        expectedAttempts++;
                        var recovered = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token);
                        CollectionAssert.AreEqual(refused.ToArray(), recovered.Take(refused.Count).ToArray());
                    }
                    Assert.IsTrue(confirmed.Receipt.MayAcknowledge, System.Text.Json.JsonSerializer.Serialize(confirmed.Receipt));
                    var confirmedAttempts = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token);
                    Assert.HasCount(expectedAttempts, confirmedAttempts);
                    CollectionAssert.AreEqual(beforeConfirmation.ToArray(), confirmedAttempts.Take(beforeConfirmation.Count).ToArray());
                    Assert.IsFalse(confirmedAttempts.Take(confirmedAttempts.Count - 1).Any(attempt => attempt.Result?.Receipt.MayAcknowledge == true));
                    Assert.AreEqual(confirmed, confirmedAttempts[^1].Result);
                    return (confirmed, confirmedAttempts);
                }

                async Task VerifyRestorationAsync()
                {
                    MirrorPulseKeepRemoteRestoreCheckpoint? cut = Enum.TryParse<MirrorPulseKeepRemoteRestoreCheckpoint>(scenario, out var value) ? value : null;
                    bool restoreInterrupted = false;
                    var restoration = new MirrorPulseConflictKeepRemoteResidentRestoration(fileSystem, catalog, state, feed, router, role, actor)
                    {
                        Checkpoint = (restorePoint, _) =>
                        {
                            if (!restoreInterrupted && restorePoint == cut)
                            {
                                restoreInterrupted = true;
                                return ValueTask.FromException(new IOException("Synthetic KeepRemote access interruption."));
                            }
                            return ValueTask.CompletedTask;
                        }
                    };
                    ValueTask<MirrorPulseConflictKeepRemoteRestoreResult> RestoreAsync() =>
                        RunPhaseAsync(stop => restoration.RestoreAsync(root.RootId, copies, stop));
                    if (cut == MirrorPulseKeepRemoteRestoreCheckpoint.AfterAccessApplied)
                    {
                        var failed = await RestoreAsync();
                        Assert.IsFalse(failed.ProductVerified);
                        Assert.AreEqual("CallbackFailed", failed.Receipt.Outcome);
                        Assert.IsTrue(failed.Receipt.AccessDescriptorApplied && failed.Receipt.AccessDescriptorReadBack && failed.Receipt.Drained);
                        Assert.IsFalse(failed.Receipt.CallbackCompleted);
                        Assert.AreEqual(failed, (await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token)).Single().Result);
                    }
                    else if (cut is not null)
                    {
                        await Assert.ThrowsExactlyAsync<IOException>(() => RestoreAsync().AsTask());
                        var pendingRestore = await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token);
                        Assert.HasCount(1, pendingRestore);
                        Assert.AreEqual(cut == MirrorPulseKeepRemoteRestoreCheckpoint.AfterRestoreRecorded, pendingRestore[0].Result is not null);
                    }
                    CloudOperationJournalEntry? successor = null;
                    byte[] later = "an ordinary edit after the retained access operation"u8.ToArray();
                    if (cut is not null && cut != MirrorPulseKeepRemoteRestoreCheckpoint.AfterRestoreStart)
                        successor = await WriteOrdinaryLaterEditAsync();
                    var restored = await RestoreAsync();
                    Assert.IsTrue(restored.ProductVerified, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        restored.Receipt,
                        restored.LinkCount,
                        restored.ObservedInSync,
                        selectedIdentityMatches = restored.ObservedPlaceholderIdentity == Convert.ToBase64String(selected.PlaceholderIdentity.Span),
                        childDescriptorMatches = restored.Child is not null && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, restored.Child.Dacl),
                        parentDescriptorMatches = restored.Parent is not null && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parent.ExpectedDacl, restored.Parent.Dacl),
                    }));
                    Assert.AreEqual(confirmed.AttemptId, restored.NativeAttemptId);
                    Assert.AreEqual("Completed", restored.Receipt.Outcome);
                    Assert.IsTrue(restored.Receipt.AccessDescriptorApplied && restored.Receipt.AccessDescriptorReadBack && restored.Receipt.Drained);
                    Assert.IsFalse(restored.Receipt.NativeIdentityPrepared || restored.Receipt.NativeConverted || restored.Receipt.DurableProjectionCommitted);
                    Assert.IsNotNull(restored.Child);
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, restored.Child.Dacl));
                    var attempts = await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token);
                    Assert.HasCount(cut is not null && cut != MirrorPulseKeepRemoteRestoreCheckpoint.AfterRestoreRecorded ? 2 : 1, attempts);
                    if (successor is null)
                    {
                        CollectionAssert.AreEqual(selectedBytes, await File.ReadAllBytesAsync(file.FullPath, token));
                        successor = await WriteOrdinaryLaterEditAsync();
                    }
                    Assert.AreEqual(restored, await RestoreAsync());
                    Assert.AreEqual(completion, await CopyAsync());
                    Assert.AreEqual(confirmed, await ConfirmAsync());
                    Assert.HasCount(attempts.Count, await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, token));
                    CollectionAssert.AreEqual(later, await File.ReadAllBytesAsync(file.FullPath, token));
                    await using (var unchanged = await state.OpenStore.BeginTransactionAsync(token))
                    {
                        var pendingSuccessor = await unchanged.Operations.GetAsync(successor.OperationId, token);
                        Assert.IsNotNull(pendingSuccessor);
                        Assert.AreEqual(successor.Sequence, pendingSuccessor.Sequence);
                        Assert.AreEqual(successor.Kind, pendingSuccessor.Kind);
                        CollectionAssert.AreEqual(successor.Payload.ToArray(), pendingSuccessor.Payload.ToArray());
                        await unchanged.RollbackAsync(token);
                    }
                    await RequirePrivateBytesAsync(copies.Local!.Data, local);
                    await RequirePrivateBytesAsync(copies.Remote.Data, selectedBytes);
                    if (scenario == "acknowledgement") await VerifyAcknowledgementAsync(successor);

                    async Task VerifyAcknowledgementAsync(CloudOperationJournalEntry laterRow)
                    {
                        var cutAt = MirrorPulseKeepRemoteAcknowledgementCheckpoint.AfterStart;
                        var acknowledger = new MirrorPulseConflictKeepRemoteResidentAcknowledgement(catalog, state, feed, router, role, actor)
                        {
                            Checkpoint = (point, _) => point == cutAt
                                ? ValueTask.FromException(new IOException("Synthetic interruption of KeepRemote acknowledgement."))
                                : ValueTask.CompletedTask
                        };
                        var originals = plan.Members.Select(member => member.OperationId).ToArray();
                        await Assert.ThrowsExactlyAsync<IOException>(() => AcknowledgeAsync(acknowledger).AsTask());
                        var pending = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token);
                        Assert.IsNotNull(pending);
                        Assert.IsNull(pending.Result);
                        await CheckOfficialRowsAsync(expectOriginals: true);
                        cutAt = MirrorPulseKeepRemoteAcknowledgementCheckpoint.AfterSdkReturn;
                        await Assert.ThrowsExactlyAsync<IOException>(() => AcknowledgeAsync(acknowledger).AsTask());
                        await CheckOfficialRowsAsync(expectOriginals: false);
                        Assert.AreEqual(pending, await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token));
                        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
                        Assert.HasCount(1, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));

                        await using var recoveredRole = new MirrorPulseNamespaceExecutionSession();
                        int sdkReturns = 0;
                        var recovered = new MirrorPulseConflictKeepRemoteResidentAcknowledgement(catalog, state, feed, router, recoveredRole, actor)
                        {
                            Checkpoint = (point, _) =>
                            {
                                if (point == MirrorPulseKeepRemoteAcknowledgementCheckpoint.AfterSdkReturn) sdkReturns++;
                                return ValueTask.CompletedTask;
                            }
                        };
                        var result = await AcknowledgeAsync(recovered);
                        Assert.AreEqual(pending.Start.InvocationId, result.InvocationId);
                        CollectionAssert.AreEqual(originals, result.VerifiedMissingOriginals.ToArray());
                        var replay = await AcknowledgeAsync(recovered);
                        Assert.AreEqual(result.InvocationId, replay.InvocationId);
                        Assert.AreEqual(result.ReadBackAt, replay.ReadBackAt);
                        Assert.AreEqual(1, sdkReturns);
                        await CheckOfficialRowsAsync(expectOriginals: false);
                        CollectionAssert.AreEqual(later, await File.ReadAllBytesAsync(file.FullPath, token));
                        Assert.AreEqual("resolved", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
                        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
                        Assert.AreEqual(sourceReads, remote.SourceReads);
                        Assert.AreEqual(0, provider.Reads);

                        ValueTask<MirrorPulseConflictKeepRemoteAcknowledgementResult> AcknowledgeAsync(MirrorPulseConflictKeepRemoteResidentAcknowledgement owner) =>
                            RunPhaseAsync(stop => owner.AcknowledgeAsync(root.RootId, copies, stop));

                        async Task CheckOfficialRowsAsync(bool expectOriginals)
                        {
                            await using var readback = await state.OpenStore.BeginTransactionAsync(token);
                            foreach (Guid originalId in originals)
                                Assert.AreEqual(expectOriginals, await readback.Operations.GetAsync(originalId, token) is not null);
                            var stillPending = await readback.Operations.GetAsync(laterRow.OperationId, token);
                            Assert.IsNotNull(stillPending);
                            Assert.AreEqual(laterRow.Sequence, stillPending.Sequence);
                            CollectionAssert.AreEqual(laterRow.Payload.ToArray(), stillPending.Payload.ToArray());
                            await readback.RollbackAsync(token);
                        }
                    }

                    async Task<CloudOperationJournalEntry> WriteOrdinaryLaterEditAsync()
                    {
                        var beforeEdit = await RetryWithRescanAsync(ReadScanAsync);
                        // This runs under the ordinary caller after the SDK scope drained.
                        await using (var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                        {
                            await target.WriteAsync(later, token); target.SetLength(later.Length); await target.FlushAsync(token);
                        }
                        while (true)
                        {
                            var page = await RetryWithRescanAsync(async () =>
                            {
                                var scan = await ReadScanAsync();
                                var page = await feed.ReadPageAsync(scan, beforeEdit.ThroughSequence, 64, token);
                                if (page.RequiresFullRescan) throw new MirrorPulseKeepRemoteJournalDeferredException(requiresFullRescan: true);
                                return page;
                            });
                            var observation = page.Changes.FirstOrDefault(change => change.ItemId == itemId && change.Kind == CloudLocalChangeKind.ContentUpdate);
                            if (observation is not null)
                            {
                                Assert.IsGreaterThan(beforeEdit.ThroughSequence, observation.Sequence);
                                Assert.IsFalse(plan.Members.Any(member => member.OperationId == observation.OperationId));
                                await using var actual = await state.OpenStore.BeginTransactionAsync(token);
                                var row = await actual.Operations.GetAsync(observation.OperationId, token);
                                Assert.IsNotNull(row); await actual.RollbackAsync(token); return row;
                            }
                            await Task.Delay(25, token);
                        }
                    }
                    async Task RequirePrivateBytesAsync(FileStream input, byte[] expected)
                    {
                        input.Position = 0;
                        using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, token);
                        CollectionAssert.AreEqual(expected, bytes.ToArray()); input.Position = 0;
                    }
                }
            }
            Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(commandId, token))!.State);
            var fencedRoots = await catalog.ReadNamespaceDispatchFencedRootsAsync(token);
            Assert.HasCount(1, fencedRoots);
            CollectionAssert.Contains(fencedRoots.ToArray(), root.RootId);
            Assert.AreEqual(sourceReads, remote.SourceReads);
            Assert.AreEqual(0, provider.Reads);
            await RetryWithRescanAsync(() => new ValueTask<MirrorPulseConflictKeepRemoteJournalBinding>(
                retainedJournal.RevalidateRetainedOriginalsAsync(copies, token)));
            await using (var pending = await state.OpenStore.BeginTransactionAsync(token))
            {
                foreach (var member in plan.Members)
                    Assert.IsNotNull(await pending.Operations.GetAsync(member.OperationId, token));
                await pending.RollbackAsync(token);
            }

            async ValueTask<CloudProtectedLocalOperationResult> ApplySealAsync()
            {
                CloudProtectedLocalOperationResult? initial = null;
                var replay = await RetryWithRescanAsync(async () =>
                {
                    try
                    {
                        return await scheduler.RunAsync(root.InstanceId, stop => actor.RunAsync(inner =>
                            new ValueTask<CloudProtectedLocalOperationResult>(seal.ApplyFirstAsync(root.RootId, copies, inner)), stop), token);
                    }
                    catch (MirrorPulseKeepRemoteJournalDeferredException error)
                    {
                        initial ??= error.SealResult;
                        throw;
                    }
                });
                return initial ?? replay;
            }

            ValueTask<T> RetryWithRescanAsync<T>(Func<ValueTask<T>> action) =>
                MirrorPulseUploadConflictActions.RetryKeepRemoteReconciliationAsync(action, ReconcileAsync, token);

            ValueTask<T> RunPhaseAsync<T>(Func<CancellationToken, Task<T>> action) => RetryWithRescanAsync(() =>
                scheduler.RunAsync(root.InstanceId, stop => actor.RunAsync(inner => new ValueTask<T>(action(inner)), stop), token));

            async ValueTask<CloudLocalChangeScan> ReadScanAsync()
            {
                var scan = await feed.BeginScanAsync(token);
                if (scan.RequiresFullRescan) throw new MirrorPulseKeepRemoteJournalDeferredException(requiresFullRescan: true);
                return scan;
            }

            async ValueTask<bool> ReconcileAsync(CancellationToken stop)
            {
                var policy = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog,
                    remote, remote, _ => true, remote, remote, remote, scheduler: scheduler, namespaceWork: work);
                var recovery = new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync,
                    feed.AcknowledgeFullRescanAsync, (_, _) => ValueTask.CompletedTask);
                await recovery.RunAsync(new(true), stop);
                Assert.Contains(root.RootId, await catalog.ReadDeferredRescanRootsAsync(stop));
                return true;
            }

            async Task<MirrorPulseJournalUploadBatch> ReadEditedWindowAsync(int expected)
            {
                while (true)
                {
                    var current = await journal.ReadPendingAsync(token);
                    Assert.IsFalse(current.RequiresFullRescan);
                    if (current.ReadyCommands.Count(command => command.ItemId == itemId) >= expected)
                        return current;
                    await Task.Delay(25, token);
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
