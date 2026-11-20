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
    public async Task NativeBornReadbackRecoversActualMovedParentAndNestedRolesWithoutChangingFirstFacts()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.born-readback"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        MirrorPulseNamespaceMemberRegistration? rootMember = null, left = null, right = null, child = null;
        MirrorPulseNamespaceBirthIntent? firstBirth = null;
        MirrorPulseNamespaceBirthObservation? firstObservation = null;
        MirrorPulseNamespaceInitializationIntent? initial = null;
        Guid initialization = Guid.NewGuid(); string? oldRole = null;
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
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, timeout.Token);
                if (owner == 0)
                {
                    oldRole = execution.RoleSid.Value;
                    Guid firstOperation = Guid.NewGuid();
                    await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                        .InitializeAsync(firstOperation, timeout.Token);
                    initial = (await catalog.ReadNamespaceInitializationStatusAsync(firstOperation, timeout.Token)).Intent;
                    var tree = await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, timeout.Token);
                    rootMember = (await catalog.ReadNamespaceMemberAsync(tree.Single(entry => entry.RelativePath == "Docs").MemberId, timeout.Token))!;
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, execution, actor);
                    left = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "left", true, timeout.Token);
                    right = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "right", true, timeout.Token);
                    child = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "empty.bin", false, timeout.Token);
                    firstBirth = await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token);
                    firstObservation = await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token);
                    var moving = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, execution, actor);
                    var move = await moving.PrepareAsync(Guid.NewGuid(), child.MemberId, right.MemberId, "moved.bin", timeout.Token);
                    await moving.ExecuteAsync(move.OperationId, timeout.Token);
                    await catalog.PrepareNamespaceInitializationAsync(new(1, initialization, initial.Roots, DateTimeOffset.UtcNow), timeout.Token);
                }
                else
                {
                    Assert.AreNotEqual(oldRole, execution.RoleSid.Value);
                    await using var anchor = await new MirrorPulseNamespaceAnchorProtectionCoordinator(fileSystem, catalog, router, execution, actor, permissions)
                        .ProtectAsync(timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, anchor.Result.Permission!.Outcome);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                        (await new MirrorPulseNamespaceOriginalMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
                            .RecoverAsync(initialization, rootMember!.MemberId, timeout.Token)).Permission!.Outcome);
                    var recovery = new MirrorPulseNamespaceBornMemberReadbackCoordinator(fileSystem, catalog, router, execution, actor, scheduler);
                    Guid childEpoch = Guid.NewGuid();
                    // A still-old born direct parent cannot supply current-role authority.
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await recovery.VerifyAsync(initialization, childEpoch, child!.MemberId, timeout.Token));
                    foreach (var parent in new[] { left!, right! })
                    {
                        var epoch = await recovery.VerifyAsync(initialization, Guid.NewGuid(), parent.MemberId, timeout.Token);
                        Assert.AreEqual(execution.RoleSid.Value, epoch.RoleSid); Assert.AreEqual(3, epoch.Version);
                    }
                    var writing = new MirrorPulseNamespaceBornMemberRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions);
                    var oldProtection = (await catalog.ReadLatestNamespaceBirthProtectionAsync(child!.OriginId, timeout.Token))!;
                    var currentIdentity = await catalog.ReadNamespaceMemberIdentityAsync(child.MemberId, timeout.Token);
                    var movedItem = fileSystem.GetFile("Docs/right/moved.bin");
                    await RunNativeMemberAdmissionAsync(actor, execution, async () =>
                    {
                        // Reproduce a retained older owned descriptor; this fixture mutation
                        // is fault injection, not a claim that MP already recorded a new write.
                        var changed = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(movedItem, router, child.LocalObject,
                            new(currentIdentity.ItemId!.Value, currentIdentity.RemoteId!),
                            async (lease, token) => await lease.ApplyDaclAsync(oldProtection.Verification.Dacl, token), timeout.Token);
                        AssertCompletedRegisteredProtectionScope(changed);
                    }, timeout.Token);
                    Guid roleAction = Guid.NewGuid();
                    var roleRestored = await writing.RecoverAsync(initialization, roleAction, child.MemberId, timeout.Token);
                    Assert.IsNotNull(roleRestored.Completion);
                    Assert.IsTrue(roleRestored.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true });
                    Assert.AreEqual(execution.RoleSid.Value, (await catalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, timeout.Token))!.RoleSid);
                    var recovered = await recovery.VerifyAsync(initialization, childEpoch, child!.MemberId, timeout.Token);
                    Assert.AreEqual(right!.MemberId, recovered.Placement!.ParentMemberId);
                    Assert.AreEqual(execution.RoleSid.Value, recovered.RoleSid);
                    Assert.AreEqual(recovered, await recovery.VerifyAsync(initialization, childEpoch, child.MemberId, timeout.Token));
                    var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.MemberId, timeout.Token))!;
                    Assert.AreEqual("Docs/right/moved.bin", snapshot.Context.Location.RelativePath);
                    Assert.AreEqual(recovered.ProtectionId, snapshot.Context.Protection.ProtectionId);
                    Assert.AreEqual(firstBirth, await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token));
                    Assert.AreEqual(firstObservation, await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token));
                    Assert.AreEqual(child, await catalog.ReadNamespaceMemberAsync(child.MemberId, timeout.Token));
                    Assert.AreEqual(0L, new FileInfo(fileSystem.GetFile(snapshot.Context.Location.RelativePath).FullPath).Length);
                    string path = fileSystem.GetFile(snapshot.Context.Location.RelativePath).FullPath;
                    const string latest = "latest ordinary born save retained during native recovery";
                    await File.WriteAllTextAsync(path, latest, timeout.Token);
                    Guid actionId = Guid.NewGuid();
                    await using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var busy = await writing.RecoverAsync(initialization, actionId, child.MemberId, timeout.Token);
                        Assert.IsNull(busy.Completion); Assert.AreEqual(CloudProtectedLocalOperationOutcome.Busy, busy.Receipt!.Outcome);
                    }
                    var actions = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(child.MemberId, timeout.Token);
                    Assert.HasCount(2, actions); Assert.AreEqual(actionId, actions[^1].ActionId); Assert.AreEqual(roleAction, actions[^1].PreviousActionId);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                        await recovery.VerifyAsync(initialization, Guid.NewGuid(), right.MemberId, timeout.Token));
                    var restored = await writing.RecoverAsync(initialization, actionId, child.MemberId, timeout.Token);
                    Assert.IsNotNull(restored.Completion); Assert.IsTrue(restored.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true });
                    Assert.AreEqual(actionId, (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.MemberId, timeout.Token))!.Context.Protection.ProtectionId);
                    Assert.AreEqual(latest, await File.ReadAllTextAsync(path, timeout.Token));
                    await File.WriteAllTextAsync(path, latest + " edited later", timeout.Token);
                    var historical = await writing.RecoverAsync(initialization, actionId, child.MemberId, timeout.Token);
                    Assert.IsTrue(historical.IsHistoricalReplay); Assert.IsNull(historical.Receipt);
                    Assert.AreEqual(restored.Completion, historical.Completion);
                    Assert.AreEqual(latest + " edited later", await File.ReadAllTextAsync(path, timeout.Token));
                    Assert.AreEqual(firstBirth, await catalog.ReadNamespaceBirthAsync(child.OriginId, timeout.Token));
                    Assert.AreEqual(firstObservation, await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, timeout.Token));
                    Assert.AreEqual(child, await catalog.ReadNamespaceMemberAsync(child.MemberId, timeout.Token));
                    // Restore the current public identity after the later ordinary save, then audit
                    // the entire live graph using the moved direct parent rather than first placement.
                    var latestRecovery = await writing.RecoverAsync(initialization, Guid.NewGuid(), child.MemberId, timeout.Token);
                    AssertCompletedRegisteredProtectionScope(latestRecovery.Receipt!);
                    var auditing = new MirrorPulseNamespaceCurrentTreeAuditCoordinator(fileSystem, catalog, router, execution, actor, permissions);
                    var audit = await auditing.AuditAsync(rootMember.ParentMemberId!.Value, anchor, timeout.Token);
                    Assert.HasCount(5, audit.Inventory.Members); Assert.AreEqual(execution.RoleSid.Value, audit.RoleSid);
                    Assert.AreEqual("Docs/right/moved.bin", audit.Inventory.Members.Single(member => member.Context.Member.MemberId == child.MemberId).Context.Location.RelativePath);
                    string unexpected = Path.Combine(fileSystem.GetDirectory("Docs/right").FullPath, "unregistered");
                    await RunNativeMemberAdmissionAsync(actor, execution, () => { Directory.CreateDirectory(unexpected); return Task.CompletedTask; }, timeout.Token);
                    try
                    {
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await auditing.AuditAsync(rootMember.ParentMemberId.Value, anchor, timeout.Token));
                    }
                    finally
                    {
                        await RunNativeMemberAdmissionAsync(actor, execution, () => { Directory.Delete(unexpected); return Task.CompletedTask; }, timeout.Token);
                    }
                    Assert.AreEqual(audit.Inventory.Fingerprint, (await auditing.AuditAsync(rootMember.ParentMemberId.Value, anchor, timeout.Token)).Inventory.Fingerprint);
                    Assert.AreEqual(latest + " edited later", await File.ReadAllTextAsync(path, timeout.Token));
                    Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization, timeout.Token)).Completion);
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    Assert.AreEqual(0, source.Reads);
                    var completed = await auditing.CompleteInitializationAsync(initialization, rootMember.ParentMemberId.Value, anchor, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, completed.InventoryKind);
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                    Guid newer = Guid.NewGuid();
                    await catalog.PrepareNamespaceInitializationAsync(new(1, newer, initial!.Roots, DateTimeOffset.UtcNow), timeout.Token);
                    await File.WriteAllTextAsync(path, latest + " after completion", timeout.Token);
                    Assert.AreEqual(completed, await auditing.CompleteInitializationAsync(initialization, rootMember.ParentMemberId.Value, anchor, timeout.Token));
                    Assert.AreEqual(latest + " after completion", await File.ReadAllTextAsync(path, timeout.Token));
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    Assert.AreEqual(0, source.Reads);
                    TestContext.WriteLine("BornReadbackRecovery: productionCoordinator=True; oldRoleDisposed=True; movedActualBornParent=True; parentFirstRecovery=True; fileAndDirectoryReadback=True; firstBirthObservationAndMembershipRetained=True; immutableEpochReplay=True; sourceReads=0; aclWritesInReadback=0; registeredBornBeforeWriteAction=True; ownedOlderDescriptorRestored=True; busyRetainsAction=True; publicSameObjectScopeDrained=True; latestOrdinaryBytesRetained=True; atomicV3ProjectionAndCompletion=True; historicalCompletionPreservesLaterEdits=True; completeCurrentGraphNativeAudit=True; unexpectedActualEntryRejected=True; currentInitializationCompletedInsideSharedAdmission=True; historicalReplayKeepsNewOwnerFence=True; treeReady=False; defaultHostStartup=False.");
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
