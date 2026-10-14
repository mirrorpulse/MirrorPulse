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
                    Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization, timeout.Token)).Completion);
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    Assert.AreEqual(0, source.Reads);
                    TestContext.WriteLine("BornReadbackRecovery: productionCoordinator=True; oldRoleDisposed=True; movedActualBornParent=True; parentFirstRecovery=True; fileAndDirectoryReadback=True; firstBirthObservationAndMembershipRetained=True; immutableEpochReplay=True; sourceReads=0; conversion=False; aclWritesInReadback=0; rootStillFenced=True; treeReady=False; defaultHostStartup=False.");
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
