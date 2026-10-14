using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
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
    public async Task NativeBornRecoveryResumesUnrecordedProjectionAcrossThreeRoleOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.born-recovery-restart"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        MirrorPulseNamespaceMemberRegistration? root = null, parent = null, child = null;
        MirrorPulseNamespaceBirthIntent? firstBirth = null;
        MirrorPulseNamespaceBirthObservation? firstObservation = null;
        MirrorPulseNamespaceBirthProtection? firstProtection = null, parentProtection = null;
        MirrorPulseNamespaceBornMemberRecoveryCompletion? lostResponse = null;
        Guid initialization = Guid.NewGuid(), interruptedAction = Guid.NewGuid(), committedAction = Guid.NewGuid();
        const string initialBytes = "ordinary bytes before interrupted recovery";
        const string latestBytes = "later edit after committed recovery and before a new Host";
        string? previousRole = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(source).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token);
                await setup.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("Docs") }) SetProtectedFixtureUserOwner(item, user.User!);
            }
            for (int owner = 0; owner < 3; owner++)
            {
                // Each iteration disposes the actual session, catalog, actor and role before the next starts.
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                Assert.AreNotEqual(previousRole, execution.RoleSid.Value);
                previousRole = execution.RoleSid.Value;
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, timeout.Token);
                if (owner == 0)
                {
                    Guid firstOperation = Guid.NewGuid();
                    await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                        .InitializeAsync(firstOperation, timeout.Token);
                    var initial = (await catalog.ReadNamespaceInitializationStatusAsync(firstOperation, timeout.Token)).Intent;
                    var tree = await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, timeout.Token);
                    root = (await catalog.ReadNamespaceMemberAsync(tree.Single(entry => entry.RelativePath == "Docs").MemberId, timeout.Token))!;
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, execution, actor);
                    parent = await creation.CreateAsync(Guid.NewGuid(), root.MemberId, "right", true, timeout.Token);
                    child = await creation.CreateAsync(Guid.NewGuid(), parent.MemberId, "note.txt", false, timeout.Token);
                    var retired = await creation.CreateAsync(Guid.NewGuid(), parent.MemberId, "removed.txt", false, timeout.Token);
                    var retirement = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, execution, actor);
                    var deletion = await retirement.PrepareAsync(Guid.NewGuid(), retired.MemberId, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, (await retirement.ExecuteAsync(deletion.OperationId, timeout.Token)).Phase);
                    firstBirth = await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token);
                    firstObservation = await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token);
                    firstProtection = await catalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, timeout.Token);
                    parentProtection = await catalog.ReadLatestNamespaceBirthProtectionAsync(parent.OriginId, timeout.Token);
                    await catalog.PrepareNamespaceInitializationAsync(new(1, initialization, initial.Roots, DateTimeOffset.UtcNow), timeout.Token);
                    await File.WriteAllTextAsync(fileSystem.GetFile("Docs/right/note.txt").FullPath, initialBytes, timeout.Token);
                    var interrupted = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                    {
                        Checkpoint = (point, _) => point == MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterScopeDrained
                            ? ValueTask.FromException(new IOException("Injected interruption after native drain and before projection.")) : ValueTask.CompletedTask
                    };
                    await Assert.ThrowsExactlyAsync<IOException>(async () => await interrupted.RecoverAsync(initialization, interruptedAction, child.MemberId, timeout.Token));
                    Assert.HasCount(1, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(child.MemberId, timeout.Token));
                    Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(interruptedAction, timeout.Token));
                    Assert.AreEqual(firstProtection, await catalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, timeout.Token));
                    Assert.AreEqual(initialBytes, await File.ReadAllTextAsync(fileSystem.GetFile("Docs/right/note.txt").FullPath, timeout.Token));
                }
                else
                {
                    await using var anchor = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                        .ProtectAsync(timeout.Token);
                    await new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                        .RecoverAsync(initialization, root!.MemberId, timeout.Token);
                    if (owner == 1)
                    {
                        // A known older owned directory descriptor exercises the actual directory write branch.
                        await RunNativeMemberAdmissionAsync(actor, execution, async () =>
                        {
                            var changed = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(fileSystem.GetDirectory("Docs/right"), router,
                                parent!.LocalObject, null, async (lease, token) => await lease.ApplyDaclAsync(parentProtection!.Verification.Dacl, token), timeout.Token);
                            AssertCompletedRegisteredProtectionScope(changed);
                        }, timeout.Token);
                    }
                    var recovery = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions);
                    var recoveredParent = await recovery.RecoverAsync(initialization, Guid.NewGuid(), parent!.MemberId, timeout.Token);
                    Assert.IsNotNull(recoveredParent.Completion); AssertCompletedRegisteredProtectionScope(recoveredParent.Receipt!);
                    Assert.AreEqual(execution.RoleSid.Value, (await catalog.ReadLatestNamespaceBirthProtectionAsync(parent.OriginId, timeout.Token))!.RoleSid);
                    string path = fileSystem.GetFile("Docs/right/note.txt").FullPath;
                    if (owner == 1)
                    {
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await recovery.RecoverAsync(initialization, interruptedAction, child!.MemberId, timeout.Token));
                        var responseLost = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                        {
                            Checkpoint = (point, _) => point == MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterCompletionRecorded
                                ? ValueTask.FromException(new IOException("Injected response loss after atomic completion.")) : ValueTask.CompletedTask
                        };
                        await Assert.ThrowsExactlyAsync<IOException>(async () => await responseLost.RecoverAsync(initialization, committedAction, child!.MemberId, timeout.Token));
                        lostResponse = await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(committedAction, timeout.Token);
                        Assert.IsNotNull(lostResponse); Assert.IsTrue(lostResponse.Drained);
                        Assert.AreEqual(committedAction, (await catalog.ReadLatestNamespaceBirthProtectionAsync(child!.OriginId, timeout.Token))!.ProtectionId);
                        Assert.AreEqual(initialBytes, await File.ReadAllTextAsync(path, timeout.Token));
                        await File.WriteAllTextAsync(path, latestBytes, timeout.Token);
                    }
                    else
                    {
                        var replay = await recovery.RecoverAsync(initialization, committedAction, child!.MemberId, timeout.Token);
                        Assert.IsTrue(replay.IsHistoricalReplay); Assert.IsNull(replay.Receipt); Assert.AreEqual(lostResponse, replay.Completion);
                        Assert.AreEqual(latestBytes, await File.ReadAllTextAsync(path, timeout.Token));
                        Guid latestAction = Guid.NewGuid();
                        var restored = await recovery.RecoverAsync(initialization, latestAction, child.MemberId, timeout.Token);
                        AssertCompletedRegisteredProtectionScope(restored.Receipt!); Assert.IsNotNull(restored.Completion);
                        var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(child.MemberId, timeout.Token);
                        Assert.HasCount(3, history); Assert.AreEqual(interruptedAction, history[1].PreviousActionId);
                        Assert.AreEqual(committedAction, history[2].PreviousActionId); Assert.AreEqual(latestAction, history[2].ActionId);
                        Assert.IsNull(await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(interruptedAction, timeout.Token));
                        Assert.AreEqual(execution.RoleSid.Value, (await catalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, timeout.Token))!.RoleSid);
                        Assert.AreEqual(latestBytes, await File.ReadAllTextAsync(path, timeout.Token));
                        Assert.AreEqual(firstBirth, await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token));
                        Assert.AreEqual(firstObservation, await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token));
                        Assert.AreEqual(child, await catalog.ReadNamespaceMemberAsync(child.MemberId, timeout.Token));
                        var audit = await new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                            .AuditAsync(root.ParentMemberId!.Value, anchor, timeout.Token);
                        Assert.HasCount(4, audit.Inventory.Members); Assert.HasCount(1, audit.Inventory.RetiredMembers);
                        Assert.AreEqual("Docs/right/removed.txt", audit.Inventory.RetiredMembers[0].Intent.RelativePath);
                        Assert.IsFalse(File.Exists(fileSystem.GetFile("Docs/right/removed.txt").FullPath));
                        Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization, timeout.Token)).Completion);
                        CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                        Assert.AreEqual(0, source.Reads);
                        TestContext.WriteLine("BornRecoveryRestart: threeActualRoleOwners=True; previousSessionsDisposed=True; nativeDrainBeforeProjectionInterrupted=True; durableImmutableSuccessor=True; directoryOwnedAclRestored=True; atomicCompletionResponseLost=True; historicalReplayHasNoNativeScope=True; latestOrdinaryBytesAndFirstFactsRetained=True; completeCurrentNativeAudit=True; retiredBirthExcludedFromLiveTree=True; sourceReads=0; rootStillFenced=True; treeReady=False; defaultHostStartup=False.");
                    }
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            RestoreRegisteredProtectionFixtureForCleanup(paths.SyncRootPath, user.User!);
            DeleteFixture(directory);
        }
    }
}
