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
    public async Task NativeObservedRootLabelRecoverySettlesOnlyOriginalMovedMembersUnderANewRoleAcrossOwners()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (string cut in new[] { "unstarted", "after-native", "after-observation", "after-projection" })
                await ProbeObservedRootLabelRecoveryAsync(disabled, cut);
    }

    private async Task ProbeObservedRootLabelRecoveryAsync(bool disabled, string cut)
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var setupSource = new ProtectedPermissionNoSourceProvider();
        var ranges = new RootLabelExecutionRejectedRanges();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
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
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    var rootMember = members.Values.Single(member => member.IsDirectory && member.RootId is not null);
                    var file = members.Values.Single(member => !member.IsDirectory);
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
                if (cut == "unstarted")
                {
                    var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.RecoverObservedRootLabelAsync(intent.OperationId, deadline.Token).AsTask());
                    StringAssert.Contains(failure.Message, "requires its original before-native start");
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token));
                    Assert.AreEqual(original, await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token));
                    Assert.AreEqual(intent, await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                    Assert.IsTrue((await fileSystem.GetDirectory("Docs").InspectAsync(deadline.Token)).Exists);
                    Assert.IsFalse((await fileSystem.GetDirectory("Renamed").InspectAsync(deadline.Token)).Exists);
                }
                else
                {
                    var completed = await coordinator.RecoverObservedRootLabelAsync(intent.OperationId, deadline.Token);
                    var receipt = (await catalog.ReadNamespaceDirectoryMoveCompletionAsync(intent.OperationId, deadline.Token))!;
                    Assert.AreEqual(completed, receipt.Root); Assert.HasCount(4, receipt.Entries);
                    Assert.AreEqual(MirrorPulseNamespaceDirectoryMovePublicOutcome.AlreadyProjected, receipt.PublicResult.Outcome);
                    Assert.IsTrue(receipt.PublicResult.NativeMoveObserved && receipt.PublicResult.DurableProjectionCommitted);
                    Assert.IsFalse(receipt.PublicResult.RequiresFullRescan);
                    Assert.AreEqual(firstStart, await catalog.ReadNamespaceMemberMoveStartAsync(intent.OperationId, deadline.Token));
                    if (firstNative is not null) Assert.AreEqual(firstNative, await catalog.ReadNamespaceMemberMoveObservationAsync(intent.OperationId, deadline.Token));
                    if (firstProjection is not null) Assert.AreEqual(firstProjection, await catalog.ReadNamespaceMemberMoveProjectionAsync(intent.OperationId, deadline.Token));
                    var label = (await catalog.ReadManagedRootRenameHistoryAsync(intent.OperationId, deadline.Token))!;
                    Assert.AreEqual(original.Proof, label.Proof); Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, label.Intent.Phase);
                    if (firstLabelNative is not null) Assert.AreEqual(firstLabelNative, label.Observation);
                    Assert.AreEqual(completed, await coordinator.RecoverObservedRootLabelAsync(intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                    Assert.AreEqual("Renamed", router.FindRegistration(root.RootId)!.DirectoryName);
                    Assert.AreEqual(root.State, router.FindRegistration(root.RootId)!.State);
                }
                foreach (var entry in sealedTree.Entries)
                {
                    string expected = cut == "unstarted" ? entry.RelativePath : "Renamed" + entry.RelativePath["Docs".Length..];
                    Assert.AreEqual(expected, (await catalog.ReadNamespaceMemberLocationAsync(entry.MemberId, deadline.Token)).RelativePath);
                    Assert.AreEqual(originalRole, (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.MemberId, deadline.Token))!.Context.Protection.RoleSid);
                    CloudItem actual = entry.IsDirectory ? fileSystem.GetDirectory(expected) : fileSystem.GetFile(expected);
                    Assert.AreEqual(entry.LocalObject, NativeMemberBinding(await actual.InspectAsync(deadline.Token)));
                }
                string payload = fileSystem.GetFile((cut == "unstarted" ? "Docs" : "Renamed") + "/left/nested/pending.bin").FullPath;
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
                Assert.AreEqual(0, setupSource.Reads); Assert.AreEqual(0, ranges.Calls);
            }
            TestContext.WriteLine($"ObservedRootLabelRecovery: state={root.State}; cut={cut}; differentRole=True; oldRoleAbsent=True; originalProof=True; originalFactsImmutable=True; entireProtectedTree=True; noNativeMoveOnRecovery=True; noDaclWrites=True; unstartedRefused={cut == "unstarted"}; ordinaryDeleteDenied=True; sourceReads=0; owners=3; currentReadinessGranted=False; beforeNativeReadmission=False; hostIntegrated=False.");
        }
        catch (Exception failure) { TestContext.WriteLine("ObservedRootLabelRecoveryFailure: " + failure); throw; }
        finally
        {
            await role.DisposeAsync();
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
