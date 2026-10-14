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
    public async Task NativeRuntimeRecoversMovedOriginalMembersAtCurrentPositionsAcrossRoleOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.original-recovery"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string originalPath = Path.Combine(paths.SyncRootPath, "Docs", "note.txt"), movedPath = Path.Combine(paths.SyncRootPath, "Docs", "moved.txt");
        const string latest = "unaccepted latest content after controlled move";
        MirrorPulseNamespaceMemberRegistration? fileMember = null, rootMember = null;
        MirrorPulseNamespaceInitializationIntent? firstAdmission = null;
        MirrorPulseNamespacePermissionBaseline? firstCapture = null;
        MirrorPulseNamespacePermissionLocalIdentity? firstIdentity = null;
        MirrorPulseNamespaceMemberMoveCompletion? move = null;
        string? firstRole = null;
        Guid initialization = Guid.NewGuid();
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
                await File.WriteAllTextAsync(originalPath, "original content", timeout.Token);
                foreach (CloudItem item in new CloudItem[] { setup.Root, setup.GetDirectory("Docs"), setup.GetFile("Docs/note.txt") })
                    SetProtectedFixtureUserOwner(item, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, mayDispatch: _ => true);
                await using var runtime = factory.Create(paths); await runtime.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    Guid operation = Guid.NewGuid();
                    var completion = await runtime.InitializeNamespaceProtectionAsync(operation, timeout.Token);
                    firstAdmission = (await runtime.ReadNamespaceInitializationStatusAsync(operation, timeout.Token)).Intent;
                    var tree = await catalog.ReadNamespaceMemberTreeAsync(completion.AnchorMemberId, timeout.Token);
                    fileMember = (await catalog.ReadNamespaceMemberAsync(tree.Single(entry => entry.RelativePath == "Docs/note.txt").MemberId, timeout.Token))!;
                    rootMember = (await catalog.ReadNamespaceMemberAsync(tree.Single(entry => entry.RelativePath == "Docs").MemberId, timeout.Token))!;
                    firstCapture = await catalog.ReadNamespacePermissionBaselineAsync(fileMember.OriginId, timeout.Token);
                    firstIdentity = await catalog.ReadNamespacePermissionLocalIdentityAsync(fileMember.OriginId, timeout.Token);
                    firstRole = execution.RoleSid.Value;
                    var admittedMove = await runtime.PrepareNamespaceMemberMoveAsync(Guid.NewGuid(), fileMember.MemberId, rootMember.MemberId, "moved.txt", timeout.Token);
                    move = await runtime.ExecuteNamespaceMemberMoveAsync(admittedMove.OperationId, timeout.Token);
                    Assert.AreEqual("Docs/moved.txt", move.RelativePath); Assert.IsFalse(File.Exists(originalPath));
                    await File.WriteAllTextAsync(movedPath, latest, timeout.Token);
                    await catalog.PrepareNamespaceInitializationAsync(new(1, initialization, firstAdmission.Roots, DateTimeOffset.UtcNow), timeout.Token);
                    var sameRole = await runtime.RecoverNamespaceOriginalMemberAsync(initialization, fileMember.MemberId, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, sameRole.Permission!.Outcome);
                    Assert.IsTrue(sameRole.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true });
                    Assert.AreEqual("Docs/note.txt", (await catalog.ReadLatestNamespacePermissionChangeAsync(fileMember.OriginId, timeout.Token))!.Intent.RelativePath);
                    Assert.AreEqual(latest, await File.ReadAllTextAsync(movedPath, timeout.Token));
                }
                else
                {
                    Assert.AreNotEqual(firstRole, execution.RoleSid.Value);
                    Assert.AreEqual(firstAdmission!.Roots[0], (await catalog.ReadNamespaceInitializationStatusAsync(initialization, timeout.Token)).Intent.Roots[0]);
                    await using (var writer = new FileStream(movedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var busy = await runtime.RecoverNamespaceOriginalMemberAsync(initialization, fileMember!.MemberId, timeout.Token);
                        Assert.AreEqual(CloudProtectedLocalOperationOutcome.Busy, busy.Receipt!.Outcome);
                    }
                    var pending = (await catalog.ReadLatestNamespacePermissionChangeAsync(fileMember!.OriginId, timeout.Token))!;
                    Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, pending.Phase);
                    Assert.AreEqual(move!.OperationId, pending.Intent.Relocation!.CompletedMoveOperationId);
                    Assert.AreEqual("Docs/moved.txt", pending.Intent.RelativePath);
                    var recovered = await runtime.RecoverNamespaceOriginalMemberAsync(initialization, fileMember.MemberId, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, recovered.Permission!.Outcome);
                    Assert.IsTrue(recovered.Receipt is { Outcome: CloudProtectedLocalOperationOutcome.Completed, Drained: true });
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(fileMember.MemberId, timeout.Token));
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                        (await runtime.RecoverNamespaceOriginalMemberAsync(initialization, rootMember!.MemberId, timeout.Token)).Permission!.Outcome);
                    Assert.AreEqual(latest, await File.ReadAllTextAsync(movedPath, timeout.Token));
                    await File.WriteAllTextAsync(movedPath, latest + " edited again", timeout.Token);
                    var replay = await runtime.RecoverNamespaceOriginalMemberAsync(initialization, fileMember.MemberId, timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, replay.Permission!.Outcome);
                    Assert.AreEqual(latest + " edited again", await File.ReadAllTextAsync(movedPath, timeout.Token));
                    Assert.AreEqual(fileMember, await catalog.ReadNamespaceMemberAsync(fileMember.MemberId, timeout.Token));
                    Assert.AreEqual(rootMember, await catalog.ReadNamespaceMemberAsync(rootMember.MemberId, timeout.Token));
                    Assert.AreEqual(firstCapture, await catalog.ReadNamespacePermissionBaselineAsync(fileMember.OriginId, timeout.Token));
                    Assert.AreEqual(firstIdentity, await catalog.ReadNamespacePermissionLocalIdentityAsync(fileMember.OriginId, timeout.Token));
                    Assert.AreEqual(move.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(fileMember.MemberId, timeout.Token)).RelativePath);
                    CollectionAssert.AreEqual(new[] { registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token)).ToArray());
                    Assert.IsNull((await catalog.ReadNamespaceInitializationStatusAsync(initialization, timeout.Token)).Completion);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(movedPath), timeout.Token));
                    TestContext.WriteLine("OriginalMemberRecovery: productionRuntime=True; oldRoleDisposed=True; realControlledMove=True; sameRoleMovedReplayWithoutAclWrite=True; pendingInitializationAcrossOwners=True; currentCommittedPosition=True; beforeWriteRotation=True; busyRetainedIntent=True; sameObjectLatestBytes=True; firstCaptureAndMembershipRetained=True; replayPreservesLaterEdits=True; nativeScopeDrained=True; rootStillFenced=True; sourceReads=0; treeReady=False; defaultHostStartup=False.");
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (File.Exists(movedPath)) RestoreOriginalMemberRecoveryFileForCleanup(directory, movedPath, user.User!);
            RestoreInitializationFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            DeleteFixture(directory);
        }
    }

    private static void RestoreOriginalMemberRecoveryFileForCleanup(string directory, string path, SecurityIdentifier user)
    {
        string expected = Path.Combine(directory, "sync", "Docs", "moved.txt");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory, ".mp-permission-fixture")))
            throw new InvalidOperationException("Original recovery cleanup must remain inside its marked fixture.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) throw new InvalidOperationException("Fixture cleanup cannot follow a link.");
        var access = new System.Security.AccessControl.FileSecurity();
        access.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;" + user.Value + ")", System.Security.AccessControl.AccessControlSections.Access);
        System.IO.FileSystemAclExtensions.SetAccessControl(info, access);
    }
}
