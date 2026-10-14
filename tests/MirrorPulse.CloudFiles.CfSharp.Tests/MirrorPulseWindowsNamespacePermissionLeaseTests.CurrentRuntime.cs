using System.Security.AccessControl;
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
    public async Task NativeRuntimeRecoversTheEvolvedCurrentGraphAcrossOwnersAndBusyRetry()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.current-runtime"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string originalPath = Path.Combine(paths.SyncRootPath, "Docs", "note.txt");
        string movedPath = Path.Combine(paths.SyncRootPath, "Docs", "right", "note.txt");
        string bornPath = Path.Combine(paths.SyncRootPath, "Docs", "right", "born.txt");
        const string originalBytes = "latest ordinary moved original edit";
        const string bornBytes = "latest ordinary moved born edit";
        MirrorPulseNamespaceMemberRegistration? moved = null, born = null, retired = null;
        MirrorPulseNamespaceBirthObservation? firstObservation = null;
        MirrorPulseNamespaceInitializationCompletion? initial = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token);
                await using var feed = setup.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
                await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, timeout.Token);
                await setup.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: timeout.Token);
                await File.WriteAllTextAsync(originalPath, "original resident content", timeout.Token);
                await File.WriteAllTextAsync(Path.Combine(paths.SyncRootPath, "Docs", "deleted.txt"), "retired original content", timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("Docs"), setup.GetFile("Docs/note.txt"), setup.GetFile("Docs/deleted.txt") })
                    SetProtectedFixtureUserOwner(item, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var admission = new MirrorPulseNamespaceWorkAdmission([registration.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, mayDispatch: _ => true, namespaceWork: admission);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                if (owner == 0)
                {
                    initial = await runtime.InitializeNamespaceProtectionAsync(Guid.NewGuid(), timeout.Token);
                    Assert.IsTrue(admission.IsAdmitted(registration.RootId));
                    var tree = await catalog.ReadNamespaceMemberTreeAsync(initial.AnchorMemberId, timeout.Token);
                    var root = tree.Single(item => item.RelativePath == "Docs");
                    moved = (await catalog.ReadNamespaceMemberAsync(tree.Single(item => item.RelativePath == "Docs/note.txt").MemberId, timeout.Token))!;
                    retired = (await catalog.ReadNamespaceMemberAsync(tree.Single(item => item.RelativePath == "Docs/deleted.txt").MemberId, timeout.Token))!;
                    var parent = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), root.MemberId, "right", true, timeout.Token);
                    born = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), parent.MemberId, "empty.txt", false, timeout.Token);
                    firstObservation = await catalog.ReadNamespaceBirthObservationAsync(born.OriginId, timeout.Token);
                    var deletion = await runtime.PrepareNamespaceMemberRetirementAsync(Guid.NewGuid(), retired.MemberId, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, (await runtime.ExecuteNamespaceMemberRetirementAsync(deletion.OperationId, timeout.Token)).Phase);
                    var originalMove = await runtime.PrepareNamespaceMemberMoveAsync(Guid.NewGuid(), moved.MemberId, parent.MemberId, "note.txt", timeout.Token);
                    await runtime.ExecuteNamespaceMemberMoveAsync(originalMove.OperationId, timeout.Token);
                    var bornMove = await runtime.PrepareNamespaceMemberMoveAsync(Guid.NewGuid(), born.MemberId, parent.MemberId, "born.txt", timeout.Token);
                    await runtime.ExecuteNamespaceMemberMoveAsync(bornMove.OperationId, timeout.Token);
                    await File.WriteAllTextAsync(movedPath, originalBytes, timeout.Token);
                    await File.WriteAllTextAsync(bornPath, bornBytes, timeout.Token);
                }
                else
                {
                    Assert.AreEqual(initial, await runtime.InitializeNamespaceProtectionAsync(initial!.OperationId, timeout.Token));
                    Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                    Guid operation = Guid.NewGuid();
                    await using (var writer = new FileStream(movedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.RecoverNamespaceCurrentTreeAsync(operation, timeout.Token));
                    Assert.IsNull((await runtime.ReadNamespaceInitializationStatusAsync(operation, timeout.Token)).Completion);
                    Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    var completion = await runtime.RecoverNamespaceCurrentTreeAsync(operation, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory, completion.InventoryKind);
                    Assert.AreNotEqual(initial!.RoleSid, completion.RoleSid); Assert.AreEqual(execution.RoleSid.Value, completion.RoleSid);
                    Assert.AreEqual(5, completion.MemberCount); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                    Assert.IsTrue(admission.IsAdmitted(registration.RootId));
                    var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(completion.AnchorMemberId, timeout.Token);
                    Assert.HasCount(5, inventory.Members); Assert.HasCount(1, inventory.RetiredMembers);
                    Assert.AreEqual(retired!.MemberId, inventory.RetiredMembers[0].Intent.MemberId);
                    Assert.AreEqual(moved, await catalog.ReadNamespaceMemberAsync(moved!.MemberId, timeout.Token));
                    Assert.AreEqual(born, await catalog.ReadNamespaceMemberAsync(born!.MemberId, timeout.Token));
                    Assert.AreEqual(firstObservation, await catalog.ReadNamespaceBirthObservationAsync(born.OriginId, timeout.Token));
                    Assert.AreEqual("Docs/right/note.txt", inventory.Members.Single(item => item.Context.Member.MemberId == moved.MemberId).Context.Location.RelativePath);
                    Assert.AreEqual("Docs/right/born.txt", inventory.Members.Single(item => item.Context.Member.MemberId == born.MemberId).Context.Location.RelativePath);
                    Assert.AreEqual(originalBytes, await File.ReadAllTextAsync(movedPath, timeout.Token));
                    Assert.AreEqual(bornBytes, await File.ReadAllTextAsync(bornPath, timeout.Token));
                    await File.WriteAllTextAsync(bornPath, bornBytes + " after completion", timeout.Token);
                    Assert.AreEqual(completion, await runtime.RecoverNamespaceCurrentTreeAsync(operation, timeout.Token));
                    Assert.AreEqual(bornBytes + " after completion", await File.ReadAllTextAsync(bornPath, timeout.Token));
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token));
                    Assert.IsTrue(admission.IsAdmitted(registration.RootId));
                    Assert.AreEqual(bornBytes + " after completion", await File.ReadAllTextAsync(bornPath, timeout.Token));
                    await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(paths.SyncRootPath, paths.SyncRootPath + ".moved"), timeout.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(movedPath), timeout.Token));
                    Assert.IsNull(await catalog.ReadMutationAsync(operation, timeout.Token));
                    TestContext.WriteLine("CurrentRuntimeRecovery: productionRuntime=True; previousRuntimeDisposed=True; movedOriginalAndBorn=True; retiredOriginalRetained=True; latestOrdinaryBytesPreserved=True; busyKeepsInitializer=True; sameOperationRetry=True; parentFirstRecovery=True; completeNativeCurrentAudit=True; currentReceiptAndOwnedFenceRelease=True; liveAnchorRetained=True; historicalReplayPreservesLaterEdit=True; rootsInitiallyClosed=True; historicalCompletionDoesNotAdmitNewHost=True; busyDoesNotAdmit=True; freshAuditAdmitsOnScopeExit=True; noRemoteAcceptance=True; defaultHostStartup=False; hostCliIntegrated=False.");
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreCurrentRuntimeFixtureForCleanup(string directory, string syncRoot, SecurityIdentifier user)
    {
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        foreach (string relative in new[] { "Docs/note.txt", "Docs/right/note.txt", "Docs/deleted.txt" })
        {
            var file = new FileInfo(Path.Combine(syncRoot, relative.Replace('/', Path.DirectorySeparatorChar))); if (!file.Exists) continue;
            if (file.LinkTarget is not null) throw new InvalidOperationException("Current runtime fixture cleanup cannot follow a file link.");
            var access = new FileSecurity(); access.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;" + user.Value + ")", AccessControlSections.Access);
            file.SetAccessControl(access);
        }
        RestoreSourceContextFixtureForCleanup(directory, syncRoot, user);
        RestoreRegisteredProtectionFixtureForCleanup(syncRoot, user);
    }
}
