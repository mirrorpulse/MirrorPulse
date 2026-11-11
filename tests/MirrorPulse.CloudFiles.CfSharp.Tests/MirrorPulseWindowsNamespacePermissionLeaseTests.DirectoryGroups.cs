using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task NativeProductionDirectoryGroupSettlesOriginalCreateMoveOrRetirementAcrossOwners(bool retire, bool interrupt) =>
        RunDirectoryGroupFixtureAsync(retire, interrupt, null);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public Task NativeProductionDirectoryGroupRereadsRealLateChildOriginalsBeforeDispatchAcrossOwners(bool transient) =>
        RunDirectoryGroupFixtureAsync(false, false, transient ? "transient-child" : "live-child");

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public Task NativeProductionDirectoryAbsencePreservesALaterBackendFileOrDirectoryAcrossOwners(bool directory) =>
        RunDirectoryGroupFixtureAsync(true, false, directory ? "backend-directory" : "backend-file");

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public Task NativeProductionDirectoryGroupRecoversAcceptedEffectsBeforeOriginalAckAcrossFreshRoles(bool retire) =>
        RunDirectoryGroupFixtureAsync(retire, true, null, freshRoles: true);

    private async Task RunDirectoryGroupFixtureAsync(bool retire, bool interrupt, string? dependency, bool freshRoles = false)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.directory-groups"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var firstRole = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(95));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var remote = new MirrorPulseDirectoryJournalTests.DirectoryTransport(Path.Combine(directory, "backend"));
        MirrorPulseNamespaceMemberRegistration created = null!;
        MirrorPulseJournalCoalescingPlan plan = null!;
        MirrorPulseNamespaceMemberRegistration? lateChild = null;
        bool childInjected = false;
        var originalFacts = new Dictionary<Guid, (long Sequence, CloudStateOperationKind Kind, string Payload)>();
        bool backendOccupant = dependency is "backend-directory" or "backend-file";
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 8, 9]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var changedRole = owner == 1 && freshRoles ? new MirrorPulseNamespaceExecutionSession() : null;
                var role = changedRole ?? firstRole;
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var feed = fileSystem.CreateLocalChangeFeed();
                if (owner == 0)
                {
                    if (freshRoles)
                    {
                        await feed.StartAsync(deadline.Token);
                        await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token);
                        await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
                        SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!);
                        SetProtectedFixtureUserOwner(fileSystem.GetDirectory("Docs"), caller.User!);
                        await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                        await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, role, actor, scheduler, permissions)
                            .InitializeAsync(Guid.NewGuid(), deadline.Token);
                        foreach (var entry in await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, deadline.Token))
                            members.Add(entry.MemberId, (await catalog.ReadNamespaceMemberAsync(entry.MemberId, deadline.Token))!);
                    }
                    else
                    {
                        Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                        await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1, 2, 3], deadline.Token);
                        await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                        await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                        await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                        await feed.StartAsync(deadline.Token);
                    }
                    var docs = members.Values.Single(value => value.IsDirectory && value.RootId is not null);
                    created = await new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor)
                        .CreateAsync(Guid.NewGuid(), docs.MemberId, "folder", true, deadline.Token);
                    if (retire)
                    {
                        var retiring = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, role, actor);
                        var intent = await retiring.PrepareAsync(Guid.NewGuid(), created.MemberId, deadline.Token);
                        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, (await retiring.ExecuteAsync(intent.OperationId, deadline.Token)).Phase);
                    }
                    else
                    {
                        var moving = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                        var intent = await moving.PrepareAsync(Guid.NewGuid(), created.MemberId, docs.MemberId, "moved-folder", deadline.Token);
                        await moving.ExecuteAsync(intent.OperationId, deadline.Token);
                    }
                    var identity = await catalog.ReadNamespaceMemberIdentityAsync(created.MemberId, deadline.Token);
                    var probe = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    MirrorPulseJournalCoalescingPlan? observed = null;
                    while (observed is null)
                    {
                        var pending = await probe.ReadPendingAsync(deadline.Token);
                        if (pending.CompleteWindow is { Count: > 1 } window)
                            observed = MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(window, identity.ItemId!.Value,
                                pending.WindowUpperSequence, new HashSet<Guid>());
                        if (observed is null) await Task.Delay(25, deadline.Token);
                    }
                    plan = observed;
                    Assert.AreEqual(retire ? MirrorPulseCoalescedEffect.VerifyDirectoryAbsence : MirrorPulseCoalescedEffect.CreateDirectory, plan.Effect);
                    await using var read = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    foreach (var member in plan.Members)
                    {
                        var row = await read.Operations.GetAsync(member.OperationId, deadline.Token); Assert.IsNotNull(row);
                        originalFacts.Add(row.OperationId, (row.Sequence, row.Kind, Convert.ToHexString(SHA256.HashData(row.Payload.Span))));
                    }
                    await read.RollbackAsync(deadline.Token);
                    if (backendOccupant)
                    {
                        Directory.CreateDirectory(remote.Root);
                        string replacement = Path.Combine(remote.Root, plan.FinalPath);
                        if (dependency == "backend-directory")
                        {
                            Directory.CreateDirectory(replacement);
                            replacement = Path.Combine(replacement, "keep.bin");
                        }
                        await File.WriteAllBytesAsync(replacement, [9, 7, 5], deadline.Token);
                    }
                }
                else await feed.StartAsync(deadline.Token);
                if (changedRole is not null)
                {
                    Assert.AreNotEqual(firstRole.RoleSid.Value, role.RoleSid.Value);
                    await using var recovered = await new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(fileSystem, catalog, router, role, actor, scheduler, permissions)
                        .RecoverRootAsync(Guid.NewGuid(), root.RootId, deadline.Token);
                }
                await using var anchor = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, role, actor, permissions)
                    .ProtectAsync(deadline.Token);
                var anchorMember = await catalog.ReadNamespaceMemberByBindingAsync(anchor.Original.LocalObject, deadline.Token); Assert.IsNotNull(anchorMember);
                await using (var opening = await work.BeginRecoveryAsync([root.RootId], deadline.Token))
                {
                    var audited = await new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, role, actor, permissions)
                        .AuditRootAsync(anchorMember.MemberId, anchor, root.RootId, deadline.Token);
                    Assert.AreEqual(role.RoleSid.Value, audited.RoleSid);
                    opening.CompleteAfterFreshAudit();
                }
                if (owner == 0 || interrupt)
                {
                    var liveProbe = new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true);
                    var live = await liveProbe.ReadPendingAsync(deadline.Token);
                    Assert.IsNotNull(live.CompleteWindow);
                    var livePlan = await catalog.ReadJournalCoalescingOwnerAsync(plan.Members[0].OperationId, deadline.Token)
                        ?? MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(live.CompleteWindow, plan.ItemId,
                            live.WindowUpperSequence, new HashSet<Guid>());
                    Assert.IsNotNull(livePlan);
                    using (work.Enter([root.RootId]))
                        await scheduler.RunAsync(root.InstanceId, async stop =>
                        {
                            await new MirrorPulseNamespaceDirectoryConfirmation(catalog, router, state, actor, role)
                                .RequireJournalGroupAsync(fileSystem, livePlan, live.CompleteWindow, live.WindowUpperSequence, stop);
                            return true;
                        }, deadline.Token);
                    var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var completion = new MirrorPulseJournalUploadCompletion(feed, state, new BackoffPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)));
                    await using (var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, remote, remote, state,
                        paths.SyncRootPath, paths.DataRootPath, _ => true, completion, mutations: remote, directories: remote,
                        fileSystem: fileSystem, scheduler: scheduler, namespaceActor: actor, namespaceExecution: role, namespaceWork: work)
                    {
                        CoalescingCheckpoint = async (phase, stop) =>
                        {
                            if (dependency is not null)
                            {
                                if (backendOccupant)
                                {
                                    if (phase == MirrorPulseJournalCoalescingCheckpoint.DirectoryEffectsDeferred) reached.TrySetResult();
                                    return;
                                }
                                if (phase == MirrorPulseJournalCoalescingCheckpoint.BeforeDirectoryCurrentWindow && !childInjected)
                                {
                                    childInjected = true;
                                    // Already inside the held instance admission, after the runner's scan.
                                    // Create real controlled SDK facts rather than supplying another feed.
                                    lateChild = await new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor)
                                        .CreateAsync(Guid.NewGuid(), created.MemberId, "late.bin", false, stop);
                                    var childIdentity = await catalog.ReadNamespaceMemberIdentityAsync(lateChild.MemberId, stop);
                                    await RetainChildOriginalAsync(CloudLocalChangeKind.Create);
                                    if (dependency == "transient-child")
                                    {
                                        var deletion = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, role, actor);
                                        var deleting = await deletion.PrepareAsync(Guid.NewGuid(), lateChild.MemberId, stop);
                                        await deletion.ExecuteAsync(deleting.OperationId, stop);
                                        await RetainChildOriginalAsync(CloudLocalChangeKind.Delete);
                                    }

                                    async Task RetainChildOriginalAsync(CloudLocalChangeKind kind)
                                    {
                                        CloudLocalChange? original = null;
                                        while (original is null)
                                        {
                                            original = (await feed.ReadBatchAsync(stop)).Changes.FirstOrDefault(change => change.ItemId == childIdentity.ItemId && change.Kind == kind);
                                            if (original is null) await Task.Delay(20, stop);
                                        }
                                        await using var observed = await state.OpenStore.BeginTransactionAsync(stop);
                                        var row = await observed.Operations.GetAsync(original.OperationId, stop); Assert.IsNotNull(row);
                                        originalFacts.Add(row.OperationId, (row.Sequence, row.Kind, Convert.ToHexString(SHA256.HashData(row.Payload.Span))));
                                        await observed.RollbackAsync(stop);
                                    }
                                }
                                if (phase == MirrorPulseJournalCoalescingCheckpoint.DirectoryCurrentWindowRejected) reached.TrySetResult();
                                return;
                            }
                            if (owner == 0 && interrupt && phase == MirrorPulseJournalCoalescingCheckpoint.AfterEffectsCompleted)
                            { reached.TrySetResult(); throw new IOException("Owned directory cut before original ACK."); }
                            if ((!interrupt || owner == 1) && phase == MirrorPulseJournalCoalescingCheckpoint.AfterOriginalsAcknowledged) reached.TrySetResult();
                        }
                    })
                    {
                        await pump.StartAsync(deadline.Token);
                        await reached.Task.WaitAsync(deadline.Token);
                    }
                }
                bool acknowledged = dependency is null && (owner == 1 || !interrupt);
                if (backendOccupant)
                {
                    var owned = await catalog.ReadJournalCoalescingOwnerAsync(plan.Members[0].OperationId, deadline.Token); Assert.IsNotNull(owned);
                    CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), owned.Members.Select(member => member.OperationId).ToArray());
                    var execution = await catalog.ReadJournalCoalescingExecutionAsync(owned.PlanId, deadline.Token); Assert.IsNotNull(execution);
                    var effect = await catalog.ReadMutationAsync(execution.Steps.Single().OperationId, deadline.Token); Assert.IsNotNull(effect);
                    Assert.AreEqual(MirrorPulseMutationState.Conflict, effect.State);
                    Assert.AreEqual(0, remote.Creates);
                    string replacement = Path.Combine(remote.Root, plan.FinalPath);
                    Assert.AreEqual(dependency == "backend-directory", Directory.Exists(replacement));
                    string retained = dependency == "backend-directory" ? Path.Combine(replacement, "keep.bin") : replacement;
                    CollectionAssert.AreEqual(new byte[] { 9, 7, 5 }, await File.ReadAllBytesAsync(retained, deadline.Token));
                }
                else if (dependency is not null)
                {
                    Assert.IsTrue(childInjected);
                    Assert.IsNull(await catalog.ReadJournalCoalescingOwnerAsync(plan.Members[0].OperationId, deadline.Token));
                    var current = await new MirrorPulseJournalUploadSource(feed, router, catalog, _ => true).ReadPendingAsync(deadline.Token);
                    Assert.IsNotNull(current.CompleteWindow);
                    var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseNamespaceDirectoryConfirmation
                        .RequireOriginalDirectoryWindow(plan, current.CompleteWindow, current.WindowUpperSequence));
                    StringAssert.StartsWith(refusal.Message, "Later or transient directory dependencies");
                    Assert.IsNull(MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(current.CompleteWindow, plan.ItemId,
                        current.WindowUpperSequence, new HashSet<Guid>()));
                    Assert.AreEqual(0, remote.Creates); Assert.IsFalse(Directory.Exists(remote.Root));
                }
                else
                {
                    var ownedPlan = await catalog.ReadJournalCoalescingOwnerAsync(plan.Members[0].OperationId, deadline.Token); Assert.IsNotNull(ownedPlan);
                    Assert.AreEqual(plan.ItemId, ownedPlan.ItemId); Assert.AreEqual(plan.Effect, ownedPlan.Effect);
                    CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), ownedPlan.Members.Select(member => member.OperationId).ToArray());
                    Assert.IsGreaterThanOrEqualTo(plan.Members.Max(member => member.Sequence), ownedPlan.WindowUpperSequence);
                    plan = ownedPlan;
                    var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, deadline.Token); Assert.IsNotNull(execution);
                    Assert.AreEqual(4, execution.Version); Assert.IsNull(execution.Content);
                    var effect = await catalog.ReadMutationAsync(execution.Steps.Single().OperationId, deadline.Token); Assert.IsNotNull(effect);
                    Assert.AreEqual(MirrorPulseMutationState.Acknowledged, effect.State); Assert.IsTrue(effect.Intent.IsDirectory);
                    Assert.IsNull(await catalog.ReadContentAcceptanceProofAsync(effect.Intent.OperationId, deadline.Token));
                    Assert.AreEqual(retire ? 0 : 1, remote.Creates);
                    Assert.AreEqual(!retire, Directory.Exists(Path.Combine(remote.Root, "moved-folder")));
                }
                Assert.AreEqual(created, await catalog.ReadNamespaceMemberAsync(created.MemberId, deadline.Token));
                Assert.AreEqual(0, remote.UnexpectedCalls);
                await using (var verify = await state.OpenStore.BeginTransactionAsync(deadline.Token))
                {
                    foreach (var original in originalFacts)
                    {
                        var retained = await verify.Operations.GetAsync(original.Key, deadline.Token);
                        if (acknowledged) Assert.IsNull(retained);
                        else
                        {
                            Assert.IsNotNull(retained); Assert.AreEqual(original.Value.Sequence, retained.Sequence);
                            Assert.AreEqual(original.Value.Kind, retained.Kind); Assert.AreEqual(original.Value.Payload, Convert.ToHexString(SHA256.HashData(retained.Payload.Span)));
                        }
                    }
                    await verify.RollbackAsync(deadline.Token);
                }
                if (owner == 1 && dependency == "live-child")
                {
                    // Remove only the owned test child after every retained-original assertion.
                    var cleanup = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, role, actor);
                    var deleting = await cleanup.PrepareAsync(Guid.NewGuid(), lateChild!.MemberId, deadline.Token);
                    await cleanup.ExecuteAsync(deleting.OperationId, deadline.Token);
                }
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine($"DirectoryGroup: productionPump=True; originalSdkJournal=True; actualOriginalAck={dependency is null}; realLateDependency={dependency is not null && !backendOccupant}; laterBackendPreserved={backendOccupant}; nativeRetirementOrIdentity=True; twoOwners=True; freshRoles={freshRoles}; actualBackend=True; noFileUpload=True; WorkerRpc=False; signedHost=False; fullS06=False.");
        }
        catch (Exception error)
        {
            TestContext.WriteLine($"DirectoryGroupFailure: category={error.GetType().Name}; hresult={error.HResult:X8}.");
            throw;
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!, moved: true);
            DeleteFixture(directory);
        }
    }
}
