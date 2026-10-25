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
    public async Task NativeRuntimeSettlesOriginalObservedRootLabelBeforeNewRoleStartupAudit()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (string cut in new[] { "after-native", "after-observation", "after-projection" })
                await ProbeRuntimeObservedRootLabelRecoveryAsync(disabled, cut);
    }

    private async Task ProbeRuntimeObservedRootLabelRecoveryAsync(bool disabled, string cut)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.observed-root-label"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        MirrorPulseNamespaceExecutionSession role = new();
        string originalRole = role.RoleSid.Value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var setupSource = new ProtectedPermissionNoSourceProvider();
        var ranges = new RootLabelExecutionRejectedRanges();
        MirrorPulseRootRenameHistory original = null!;
        MirrorPulseNamespaceMemberMoveIntent intent = null!;
        MirrorPulseNamespaceDirectoryMoveSnapshot sealedTree = null!;
        MirrorPulseNamespaceMemberMoveStart? firstStart = null;
        MirrorPulseNamespaceMemberMoveObservation? firstNative = null;
        MirrorPulseNamespaceMemberMoveProjection? firstProjection = null;
        MirrorPulseRootRenameObservation? firstLabelNative = null;
        byte[] bytes = [1, 2, 3, 5, 8, 13];
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                if (owner == 2)
                {
                    // The previous loop has drained/disposed its actual SDK/catalog first.
                    await role.DisposeAsync();
                    role = new MirrorPulseNamespaceExecutionSession();
                    Assert.AreNotEqual(originalRole, role.RoleSid.Value);
                    await role.RunNamespaceOperationAsync(() =>
                    {
                        using WindowsIdentity current = WindowsIdentity.GetCurrent();
                        Assert.IsFalse(new WindowsPrincipal(current).IsInRole(new SecurityIdentifier(originalRole)));
                        return Task.CompletedTask;
                    });
                }
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor();
                if (owner == 2)
                {
                    await using var scheduler = new MirrorPulseInstanceScheduler();
                    await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                    Assert.IsFalse(work.IsAdmitted(root.RootId));
                    var demand = new MirrorPulseDemandProvider(router, ranges, namespaceWork: work, catalog: catalog);
                    var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: demand, router: router, catalog: catalog,
                        namespaceExecution: role, namespaceActor: actor, scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true);
                    await using var runtime = factory.Create(paths);
                    await runtime.StartAsync(deadline.Token);
                    var recovery = runtime.RootRenameRecoveryResults.Single(result => result.OperationId == intent.OperationId);
                    Assert.IsNull(recovery.ErrorCode);
                    Assert.IsNotNull(recovery.Recovery);
                    Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, recovery.Recovery.Intent.Phase);
                    Assert.IsNull(recovery.Recovery.LibraryResult);
                    var current = runtime.NamespaceStartupResults.Single(result => result.RootId == root.RootId);
                    Assert.IsTrue(current.Admitted, current.RecoveryCode);
                    Assert.IsTrue(work.IsAdmitted(root.RootId));
                    var completed = (await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token))!;
                    Assert.HasCount(4, completed.Entries);
                    Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                    if (firstNative is not null) Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token));
                    if (firstProjection is not null) Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token));
                    var history = (await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token))!;
                    Assert.AreEqual(original.Proof, history.Proof);
                    if (firstLabelNative is not null) Assert.AreEqual(firstLabelNative, history.Observation);
                    var retainedTree = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(intent.OperationId, deadline.Token))!;
                    CollectionAssert.AreEqual(sealedTree.Entries.ToArray(), retainedTree.Entries.ToArray());
                    foreach (var entry in sealedTree.Entries)
                    {
                        string expected = "Renamed" + entry.RelativePath["Docs".Length..];
                        var actual = await runtime.InspectNamespaceMemberAsync(expected, entry.IsDirectory, deadline.Token);
                        Assert.AreEqual(entry.MemberId, actual.MemberId);
                        var protection = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.MemberId, deadline.Token))!;
                        Assert.AreEqual(role.RoleSid.Value, protection.Context.Protection.RoleSid);
                        Assert.AreEqual(entry.LocalObject, protection.Context.Member.LocalObject);
                    }
                    Assert.AreEqual("Renamed", router.FindRegistration(root.RootId)!.DirectoryName);
                    Assert.AreEqual(root.State, router.FindRegistration(root.RootId)!.State);
                    string payload = Path.Combine(paths.SyncRootPath, "Renamed", "left", "nested", "pending.bin");
                    CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
                    Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                    Assert.AreEqual(0, setupSource.Reads); Assert.AreEqual(0, ranges.Calls);
                    continue;
                }

                var state = new MirrorPulseCfSharpStateSession(paths);
                ICloudFileContentProvider provider = owner == 0 ? setupSource : new MirrorPulseDemandProvider(router, ranges, catalog: catalog);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await role.RunNormalUserOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                if (owner == 0)
                {
                    await using (var feed = fileSystem.CreateLocalChangeFeed())
                    {
                        await feed.StartAsync(deadline.Token);
                        await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token);
                    }
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, bytes, deadline.Token);
                    foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetFile("Docs/original.bin") })
                        SetProtectedFixtureUserOwner(item, caller.User!);
                    await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await using var initializationScheduler = new MirrorPulseInstanceScheduler();
                    await using var initializationPermissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                    await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, role,
                        actor, initializationScheduler, initializationPermissions).InitializeAsync(Guid.NewGuid(), deadline.Token);
                    Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory, initialized.Completion.InventoryKind);
                    var captures = await catalog.ReadNamespacePermissionTreesAsync(root.RootId, cancellationToken: deadline.Token);
                    Assert.HasCount(1, captures);
                    Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Sealed, captures[0].Phase);
                    Assert.AreEqual(2, captures[0].CapturedMembers);
                    var rootMember = (await catalog.ReadNamespaceMemberChildAsync(initialized.Completion.AnchorMemberId, "Docs", deadline.Token))!;
                    var file = (await catalog.ReadNamespaceMemberChildAsync(rootMember.MemberId, "original.bin", deadline.Token))!;
                    Assert.IsNotNull(rootMember);
                    Assert.IsNotNull(file);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    var left = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "left", true, deadline.Token);
                    var nested = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "nested", true, deadline.Token);
                    var prior = await coordinator.PrepareAsync(Guid.NewGuid(), file.MemberId, nested.MemberId, "pending.bin", deadline.Token);
                    await coordinator.ExecuteAsync(prior.OperationId, deadline.Token);
                    if (disabled)
                    {
                        await catalog.SetInstanceEnabledAsync(root.InstanceId, false, deadline.Token);
                        root = (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
                        router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(deadline.Token));
                    }
                    await role.RunNamespaceOperationAsync(async () => original = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                        .PrepareAsync(root.RootId, "Renamed", deadline.Token));
                    intent = await coordinator.PrepareRootLabelAsync(original.Intent.OperationId, deadline.Token);
                    sealedTree = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(intent.OperationId, deadline.Token))!;
                    Assert.HasCount(4, sealedTree.Entries);
                    Assert.AreEqual(prior.OperationId, sealedTree.Entries.Single(entry => entry.MemberId == file.MemberId).PreviousMoveOperationId);
                    continue;
                }
                if (owner == 1)
                {
                    if (cut != "unstarted")
                    {
                        var interrupted = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor,
                            (checkpoint, _) =>
                            {
                                if (cut == "after-native" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove ||
                                    cut == "after-observation" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation ||
                                    cut == "after-projection" && checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection)
                                    throw new IOException("Injected original observed root Label interruption.");
                                return ValueTask.CompletedTask;
                            });
                        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ExecuteRootLabelAsync(intent.OperationId, deadline.Token).AsTask());
                        StringAssert.Contains(failure.Message, "Injected original observed root Label interruption.");
                    }
                    firstStart = await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token);
                    firstNative = await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token);
                    firstProjection = await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token);
                    firstLabelNative = (await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token))!.Observation;
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token));
                    Assert.AreEqual(root, (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single());
                    continue;
                }
                throw new InvalidOperationException("Unexpected runtime root recovery fixture owner.");
            }
            TestContext.WriteLine($"RuntimeObservedRootLabelRecovery: state={root.State}; cut={cut}; actualRuntime=True; originalAtomicSettlementBeforeRoleRotation=True; differentRole=True; oldRoleAbsent=True; originalProofAndFacts=True; completeCurrentAudit=True; currentRoleProtectedTree=True; currentRootAdmitted=True; stableIDs=True; wholeBytes=True; ordinaryDeleteDenied=True; sourceReads=0; owners=3; processRestart=False; signedWorker=False; publicRenameEntry=False.");
        }
        catch (Exception failure) { TestContext.WriteLine("RuntimeObservedRootLabelRecoveryFailure: " + failure); throw; }
        finally
        {
            await role.DisposeAsync();
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
