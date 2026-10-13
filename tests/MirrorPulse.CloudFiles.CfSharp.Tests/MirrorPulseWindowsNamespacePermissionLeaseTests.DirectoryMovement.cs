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
    public async Task NativeControlledDirectoryMovementSettlesEveryMemberAndRecoversAcrossOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.directory-movement"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var originalMembers = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseNamespaceMemberRegistration moving = null!, right = null!, file = null!;
        MirrorPulseNamespaceMemberMoveIntent intent = null!;
        MirrorPulseNamespaceMemberMoveStart? firstStart = null;
        MirrorPulseNamespaceMemberMoveObservation? firstNative = null;
        MirrorPulseNamespaceMemberMoveProjection? firstProjection = null;
        Guid priorFileMove = Guid.Empty;
        byte[] payload = [1, 2, 3, 5, 8, 13];
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 4; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, payload, deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                }
                await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, originalMembers, actor, deadline.Token);
                    var docs = originalMembers.Values.Single(member => member.IsDirectory && member.RootId is not null);
                    file = originalMembers.Values.Single(member => !member.IsDirectory);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    moving = await creation.CreateAsync(Guid.NewGuid(), docs.MemberId, "left", true, deadline.Token);
                    right = await creation.CreateAsync(Guid.NewGuid(), docs.MemberId, "right", true, deadline.Token);
                    var nested = await creation.CreateAsync(Guid.NewGuid(), moving.MemberId, "nested", true, deadline.Token);
                    foreach (var member in new[] { moving, nested, file }) members.Add(member.MemberId, member);
                    var fileMove = await coordinator.PrepareAsync(Guid.NewGuid(), file.MemberId, nested.MemberId, "pending.bin", deadline.Token);
                    priorFileMove = fileMove.OperationId;
                    await coordinator.ExecuteAsync(priorFileMove, deadline.Token);
                    // A role-authorized but unregistered object must prevent preparing a subtree.
                    CloudFile unregistered = fileSystem.GetFile("Docs/left/unregistered.bin");
                    await role.RunNamespaceOperationAsync(async () => await File.WriteAllBytesAsync(unregistered.FullPath, [9], deadline.Token));
                    try
                    {
                        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareAsync(Guid.NewGuid(), moving.MemberId,
                            right.MemberId, "moved", deadline.Token).AsTask());
                    }
                    finally { await role.RunNamespaceOperationAsync(() => { File.Delete(unregistered.FullPath); return Task.CompletedTask; }); }
                    intent = await coordinator.PrepareAsync(Guid.NewGuid(), moving.MemberId, right.MemberId, "moved", deadline.Token);
                    Assert.IsNotNull(intent.DirectoryMoveEvidence);
                    Assert.HasCount(3, (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(intent.OperationId, deadline.Token))!.Entries);
                    Assert.IsTrue((await fileSystem.GetDirectory("Docs/left").InspectAsync(deadline.Token)).Exists);
                    Assert.IsFalse((await fileSystem.GetDirectory(intent.Target.RelativePath).InspectAsync(deadline.Token)).Exists);
                }
                else Assert.AreEqual(intent, await catalog.ReadNamespaceMemberMoveAsync(intent.OperationId, deadline.Token));
                if (owner < 3)
                {
                    var stopAt = new[] { MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove,
                        MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeObservation, MirrorPulseNamespaceMemberMoveCheckpoint.AfterOfficialProjection }[owner];
                    var interrupted = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor,
                        (checkpoint, durable) =>
                        {
                            Assert.IsFalse(durable.CanBeCanceled);
                            if (checkpoint == stopAt) throw new IOException("Injected directory product interruption.");
                            return ValueTask.CompletedTask;
                        });
                    var failure = await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.ExecuteAsync(intent.OperationId, deadline.Token).AsTask());
                    StringAssert.Contains(failure.Message, "Injected directory product interruption.");
                    firstStart ??= await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token);
                    Assert.IsNotNull(firstStart);
                    Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberPositionAsync(moving.MemberId, intent.OperationId, deadline.Token));
                    Assert.AreEqual("Docs/left", (await catalog.ReadNamespaceMemberLocationAsync(moving.MemberId, deadline.Token)).RelativePath);
                    Assert.AreEqual(intent, await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, deadline.Token));
                    var native = await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token);
                    if (owner == 0) Assert.IsNull(native);
                    else { firstNative ??= native; Assert.IsNotNull(firstNative); Assert.AreEqual(firstNative, native); }
                    var projected = await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token);
                    if (owner < 2) Assert.IsNull(projected);
                    else { firstProjection = projected; Assert.IsNotNull(firstProjection); }
                }
                else
                {
                    var completion = await coordinator.ExecuteAsync(intent.OperationId, deadline.Token);
                    Assert.AreEqual(completion, await coordinator.ExecuteAsync(intent.OperationId, deadline.Token));
                    var subtree = await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token);
                    Assert.IsNotNull(subtree); Assert.HasCount(3, subtree.Entries);
                    Assert.AreEqual(MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected, subtree.PublicResult.Outcome);
                    Assert.IsTrue(subtree.PublicResult.NativeMoveObserved && subtree.PublicResult.DurableProjectionCommitted);
                    Assert.IsFalse(subtree.PublicResult.RequiresFullRescan);
                    Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(intent.RootId, deadline.Token));
                    foreach (var entry in subtree.Entries)
                    {
                        Assert.AreEqual(members[entry.MemberId], await catalog.ReadNamespaceMemberAsync(entry.MemberId, deadline.Token));
                        Assert.AreEqual(entry.Completion.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, deadline.Token)).RelativePath);
                        Assert.AreEqual(intent.OperationId, (await catalog.ReadNamespaceMemberPositionAsync(entry.MemberId, intent.OperationId, deadline.Token))!.OperationId);
                    }
                    Assert.AreEqual(priorFileMove, (await catalog.ReadNamespaceMemberLocationAsync(file.MemberId, deadline.Token)).CompletedMoveOperationId);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    var subsequent = await creation.CreateAsync(Guid.NewGuid(), moving.MemberId, "after-move.bin", false, deadline.Token);
                    Assert.AreEqual(2, subsequent.Version);
                    Assert.AreEqual(intent.OperationId, subsequent.FirstParentPositionOperationId);
                    Assert.AreEqual("Docs/right/moved/after-move.bin", subsequent.RelativePath);
                    Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token));
                    Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token));
                    var caseOnly = await coordinator.PrepareAsync(Guid.NewGuid(), moving.MemberId, right.MemberId, "Moved", deadline.Token);
                    var interruptedCase = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor,
                        (checkpoint, _) =>
                        {
                            if (checkpoint == MirrorPulseNamespaceMemberMoveCheckpoint.AfterNativeMove)
                                throw new IOException("Injected case-only interruption.");
                            return ValueTask.CompletedTask;
                        });
                    var caseFailure = await Assert.ThrowsExactlyAsync<IOException>(() => interruptedCase.ExecuteAsync(caseOnly.OperationId, deadline.Token).AsTask());
                    StringAssert.Contains(caseFailure.Message, "Injected case-only interruption.");
                    await coordinator.ExecuteAsync(caseOnly.OperationId, deadline.Token);
                    var caseSettlement = await catalog.ReadNamespaceDirectoryMoveCompletionAsync(caseOnly.OperationId, deadline.Token);
                    Assert.IsNotNull(caseSettlement); Assert.HasCount(4, caseSettlement.Entries);
                    Assert.AreEqual(MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected, caseSettlement.PublicResult.Outcome);
                    Assert.AreEqual("Docs/right/Moved", (await catalog.ReadNamespaceMemberLocationAsync(moving.MemberId, deadline.Token)).RelativePath);
                }
                CloudFile actual = fileSystem.GetFile("Docs/right/moved/nested/pending.bin");
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(actual.FullPath, deadline.Token));
                Assert.AreEqual(file.LocalObject, NativeMemberBinding(await actual.InspectAsync(deadline.Token)));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(actual.FullPath), deadline.Token));
                Assert.IsFalse((await fileSystem.GetDirectory("Docs/left").InspectAsync(deadline.Token)).Exists);
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("ControlledDirectoryMove: publicProof=True; completeActualSubtree=True; unregisteredRejected=True; earlierChildMove=True; allPositionsAtomic=True; threeInterruptions=True; fourOwners=True; originalFactsUnchanged=True; residentBytesPreserved=True; subsequentPlacedParentCreation=True; caseOnlyRecovery=True; ordinaryDeletionDenied=True; noSourceAccess=True; hostIntegrated=False.");
        }
        catch (Exception error) { TestContext.WriteLine("ControlledDirectoryMoveFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreRegisteredProtectionFixtureForCleanup(paths.SyncRootPath, caller.User!);
            RestoreDirectoryMovePayloadForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static MirrorPulseLocalFileBinding NativeMemberBinding(CloudItemSnapshot snapshot)
    {
        Assert.IsNotNull(snapshot.LocalBinding);
        return new(snapshot.LocalBinding.VolumeSerialNumber, snapshot.LocalBinding.SyncRootFileId, snapshot.LocalBinding.LocalFileId);
    }

    private static void RestoreDirectoryMovePayloadForCleanup(string syncRoot, SecurityIdentifier user)
    {
        // The preceding root helper authenticates the exact GUID fixture and marker. The
        // captured file keeps its explicit original ACL when moved; inheritance cannot reset it.
        foreach (string path in new[] { "Docs/original.bin", "Docs/left/nested/pending.bin", "Docs/right/moved/nested/pending.bin" })
        {
            var info = new FileInfo(Path.Combine(syncRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists) continue;
            if (info.LinkTarget is not null) throw new InvalidOperationException("Directory move cleanup cannot follow a file link.");
            var cleanup = new FileSecurity();
            cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            cleanup.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(cleanup);
        }
    }
}
