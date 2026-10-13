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
    public async Task NativeRegisteredBornProtectionUsesMovedParentAndRotatedRoleAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.registered-protection"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var initialRole = new MirrorPulseNamespaceExecutionSession();
        await using var currentRole = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var originalMembers = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseNamespaceMemberRegistration childMember = null!, targetMember = null!;
        MirrorPulseNamespaceBirthIntent firstBirth = null!;
        MirrorPulseNamespaceBirthObservation firstObservation = null!;
        MirrorPulseNamespaceBirthProtection finalEpoch = null!;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 8, 9]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await (owner == 0 ? initialRole : currentRole).RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, [1, 2, 3], deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, initialRole, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                }
                await using var rootGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, initialRole, originals, originalMembers, actor, deadline.Token);
                    var docsMember = originalMembers.Values.Single(member => member.IsDirectory && member.RootId is not null);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, initialRole, actor);
                    var left = await creation.CreateAsync(Guid.NewGuid(), docsMember.MemberId, "left", true, deadline.Token);
                    targetMember = await creation.CreateAsync(Guid.NewGuid(), docsMember.MemberId, "right", true, deadline.Token);
                    childMember = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "empty.bin", false, deadline.Token);
                    firstBirth = (await catalog.ReadNamespaceBirthAsync(childMember.OriginId, deadline.Token))!;
                    firstObservation = (await catalog.ReadNamespaceBirthObservationAsync(childMember.OriginId, deadline.Token))!;
                    var moving = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, initialRole, actor);
                    var move = await moving.PrepareAsync(Guid.NewGuid(), childMember.MemberId, targetMember.MemberId, "moved.bin", deadline.Token);
                    await moving.ExecuteAsync(move.OperationId, deadline.Token);
                    var rotations = originals.Select(original => new MirrorPulseNamespacePermissionPreparation(original.Baseline,
                        original.Intent with
                        {
                            OperationId = Guid.NewGuid(),
                            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                            ExpectedDacl = original.Intent.TargetDacl,
                            RoleSid = currentRole.RoleSid.Value,
                            TargetDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(currentRole, original.Baseline.IsDirectory),
                            PreparedAt = DateTimeOffset.UtcNow
                        })).ToArray();
                    await catalog.PrepareNamespacePermissionChangesAsync(rotations, deadline.Token);
                    await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                    await currentRole.RunNamespaceOperationAsync(async () =>
                    {
                        foreach (var rotation in rotations.Reverse())
                            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                                (await permissions.ApplyProtectedAsync(rotation.Intent.OperationId,
                                    NativeOriginalMemberItem(fileSystem, rotation.Baseline), router, cancellationToken: deadline.Token)).Permission!.Outcome);
                    });
                    await RunNativeMemberAdmissionAsync(actor, currentRole, async () =>
                    {
                        await using var docsGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.GetDirectory("Docs"), router, deadline.Token);
                        foreach (var parent in new[] { left, targetMember })
                            _ = await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, Guid.NewGuid(), parent.MemberId,
                                fileSystem.GetDirectory(parent.RelativePath), fileSystem.GetDirectory("Docs"), router, actor, deadline.Token);
                    }, deadline.Token);
                }
                var actual = await catalog.ReadNamespaceMemberLocationAsync(childMember.MemberId, deadline.Token);
                Assert.AreEqual(targetMember.MemberId, actual.ParentMemberId);
                CloudFile child = fileSystem.GetFile(actual.RelativePath);
                CloudDirectory parentDirectory = fileSystem.GetDirectory(targetMember.RelativePath);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(
                    catalog, Guid.NewGuid(), childMember.MemberId, child, parentDirectory, router, actor, deadline.Token));
                await actor.RunAsync(async _ =>
                {
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(
                        catalog, Guid.NewGuid(), childMember.MemberId, child, parentDirectory, router, actor, deadline.Token));
                    return true;
                }, deadline.Token);
                await RunNativeMemberAdmissionAsync(actor, currentRole, async () =>
                {
                    await using var docsGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.GetDirectory("Docs"), router, deadline.Token);
                    await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parentDirectory, router, deadline.Token);
                    await Assert.ThrowsAsync<InvalidDataException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(
                        catalog, Guid.NewGuid(), childMember.MemberId, child, fileSystem.GetDirectory("Docs/left"), router, actor, deadline.Token));
                    if (owner == 0)
                    {
                        var current = await child.InspectAsync(deadline.Token);
                        var identity = CloudPlaceholderIdentity.Decode(current.PlaceholderIdentity.Span);
                        string expected = null!;
                        var inspection = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, childMember.LocalObject, identity,
                            async (lease, token) => { expected = (await lease.InspectAsync(token)).Dacl; }, deadline.Token);
                        AssertCompletedRegisteredProtectionScope(inspection);
                        try
                        {
                            var changed = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, childMember.LocalObject, identity,
                                async (lease, token) =>
                                {
                                    await lease.ApplyDaclAsync(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(initialRole, false), token);
                                }, deadline.Token);
                            AssertCompletedRegisteredProtectionScope(changed);
                            await Assert.ThrowsAsync<InvalidDataException>(() => MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(
                                catalog, Guid.NewGuid(), childMember.MemberId, child, parentDirectory, router, actor, deadline.Token));
                        }
                        finally
                        {
                            var restored = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(child, router, childMember.LocalObject, identity,
                                async (lease, token) => { await lease.ApplyDaclAsync(expected, token); }, deadline.Token);
                            AssertCompletedRegisteredProtectionScope(restored);
                        }
                        finalEpoch = await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, Guid.NewGuid(), childMember.MemberId,
                            child, parentDirectory, router, actor, deadline.Token);
                        Assert.AreEqual(3, finalEpoch.Version);
                        Assert.AreEqual(currentRole.RoleSid.Value, finalEpoch.RoleSid);
                        Assert.AreEqual(firstBirth.BornParent, finalEpoch.BornParent);
                        Assert.AreEqual(targetMember.MemberId, finalEpoch.Placement!.ParentMemberId);
                        Assert.AreEqual(actual.CurrentPositionOperationId, finalEpoch.Placement.PositionOperationId);
                    }
                    Assert.AreEqual(finalEpoch, await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, finalEpoch.ProtectionId,
                        childMember.MemberId, child, parentDirectory, router, actor, deadline.Token));
                }, deadline.Token);
                Assert.AreEqual(firstBirth, await catalog.ReadNamespaceBirthAsync(childMember.OriginId, deadline.Token));
                Assert.AreEqual(firstObservation, await catalog.ReadNamespaceBirthObservationAsync(childMember.OriginId, deadline.Token));
                Assert.AreEqual(childMember, await catalog.ReadNamespaceMemberAsync(childMember.MemberId, deadline.Token));
                Assert.AreEqual(finalEpoch.ProtectionId, (await catalog.ReadNamespaceMemberProtectionEpochAsync(childMember.MemberId, deadline.Token)).ProtectionId);
                Assert.AreEqual(0L, new FileInfo(child.FullPath).Length);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(child.FullPath), deadline.Token));
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("RegisteredBornProtection: currentParent=True; rotatedRole=True; publicIdentity=True; nativeBindingAndDescriptor=True; wrongParentAndDescriptorRejected=True; actorAndRoleRequired=True; firstBirthAndMembershipUnchanged=True; immutableReplay=True; twoOwners=True; noSourceAccess=True; hostIntegrated=False.");
        }
        catch (Exception error) { TestContext.WriteLine("RegisteredBornProtectionFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreRegisteredProtectionFixtureForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static void AssertCompletedRegisteredProtectionScope(CloudProtectedLocalOperationResult receipt)
    {
        Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, receipt.Outcome, receipt.Error?.ToString());
        Assert.IsTrue(receipt.CallbackStarted && receipt.CallbackCompleted && receipt.Drained);
    }

    private static void RestoreRegisteredProtectionFixtureForCleanup(string syncRoot, SecurityIdentifier user)
    {
        // The caller has verified the exact GUID fixture, marker and root before this helper.
        foreach (string name in new[] { "left", "right" })
        {
            var info = new DirectoryInfo(Path.Combine(syncRoot, "Docs", name));
            if (!info.Exists) continue;
            if (info.LinkTarget is not null)
                throw new InvalidOperationException("Registered protection fixture cleanup cannot follow a directory link.");
            var cleanup = new DirectorySecurity();
            cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            cleanup.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(cleanup);
        }
    }
}
