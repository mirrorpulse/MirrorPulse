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
    public async Task NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners()
    {
        await ProbeProtectedRootLabelPreparationAsync(disabled: false);
        await ProbeProtectedRootLabelPreparationAsync(disabled: true);
    }

    private async Task ProbeProtectedRootLabelPreparationAsync(bool disabled)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.root-label-subtree"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseRootRenameHistory originalLabel = null!;
        MirrorPulseNamespaceMemberMoveIntent admitted = null!;
        MirrorPulseNamespaceDirectoryMoveSnapshot sealedTree = null!;
        MirrorPulseNamespaceMemberRegistration rootMember = null!;
        byte[] bytes = [1, 2, 3, 5, 8, 13];
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(timeout.Token));
                var coordinator = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                if (owner == 0)
                {
                    // Create the genuine public SDK root identity before protection. This
                    // preparation-only fixture does not claim a running journal/ACK outcome.
                    await using (var feed = fileSystem.CreateLocalChangeFeed())
                    {
                        await feed.StartAsync(timeout.Token);
                        await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    }
                    await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, bytes, timeout.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, timeout.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, timeout.Token);
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, timeout.Token);
                    rootMember = members.Values.Single(member => member.IsDirectory && member.RootId is not null);
                    var originalFile = members.Values.Single(member => !member.IsDirectory);
                    var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                    var left = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "left", true, timeout.Token);
                    var nested = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "nested", true, timeout.Token);
                    var childMove = await coordinator.PrepareAsync(Guid.NewGuid(), originalFile.MemberId, nested.MemberId, "pending.bin", timeout.Token);
                    await coordinator.ExecuteAsync(childMove.OperationId, timeout.Token);
                    if (disabled)
                    {
                        await catalog.SetInstanceEnabledAsync(root.InstanceId, false, timeout.Token);
                        root = (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single();
                        router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(timeout.Token));
                        Assert.AreEqual(RootRegistrationState.Disabled, root.State);
                    }
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        originalLabel = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                            .PrepareAsync(root.RootId, "Renamed", timeout.Token);
                    });
                    var unregistered = fileSystem.GetFile("Docs/unregistered.bin");
                    await role.RunNamespaceOperationAsync(async () => await File.WriteAllBytesAsync(unregistered.FullPath, bytes, timeout.Token));
                    try
                    {
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator.PrepareRootLabelAsync(originalLabel.Intent.OperationId, timeout.Token).AsTask());
                        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(originalLabel.Intent.OperationId, timeout.Token));
                        Assert.AreEqual(originalLabel, await catalog.ReadManagedRootRenameHistoryAsync(originalLabel.Intent.OperationId, timeout.Token));
                    }
                    finally { await role.RunNamespaceOperationAsync(() => { File.Delete(unregistered.FullPath); return Task.CompletedTask; }); }
                    // Model a lost client return after the complete preparation, without
                    // inventing a product/SDK exception or a second preparation ID.
                    var lostReturn = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    {
                        _ = await coordinator.PrepareRootLabelAsync(originalLabel.Intent.OperationId, timeout.Token);
                        throw new IOException("Synthetic client return loss after the complete original subtree admission.");
                    });
                    StringAssert.Contains(lostReturn.Message, "Synthetic client return loss after the complete original subtree admission.");
                    admitted = (await catalog.ReadNamespaceMemberMoveAsync(originalLabel.Intent.OperationId, timeout.Token))!;
                    Assert.IsNotNull(admitted);
                    sealedTree = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(admitted.OperationId, timeout.Token))!;
                    Assert.IsNotNull(sealedTree);
                    Assert.AreEqual(3, admitted.Version); Assert.AreEqual(rootMember.MemberId, admitted.MemberId);
                    Assert.AreEqual(originalLabel.Proof!.DirectoryMoveEvidence, admitted.DirectoryMoveEvidence);
                    Assert.HasCount(4, sealedTree.Entries);
                    Assert.AreEqual(childMove.OperationId, sealedTree.Entries.Single(entry => entry.MemberId == originalFile.MemberId).PreviousMoveOperationId);
                }
                Assert.AreEqual(admitted, await coordinator.PrepareRootLabelAsync(originalLabel.Intent.OperationId, timeout.Token));
                Assert.AreEqual(originalLabel, await catalog.ReadManagedRootRenameHistoryAsync(originalLabel.Intent.OperationId, timeout.Token));
                var retained = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(admitted.OperationId, timeout.Token))!;
                Assert.AreEqual(sealedTree.PublicEvidenceSha256, retained.PublicEvidenceSha256);
                CollectionAssert.AreEqual(sealedTree.Entries.ToArray(), retained.Entries.ToArray());
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(admitted.OperationId, timeout.Token));
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(admitted.OperationId, timeout.Token));
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(admitted.OperationId, timeout.Token));
                Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(admitted.OperationId, timeout.Token));
                Assert.AreEqual(admitted, await catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, timeout.Token));
                Assert.AreEqual("Docs", (await catalog.ReadNamespaceMemberLocationAsync(rootMember.MemberId, timeout.Token)).RelativePath);
                Assert.AreEqual(root, (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single());
                Assert.IsTrue((await fileSystem.GetDirectory("Docs").InspectAsync(timeout.Token)).Exists);
                Assert.IsFalse((await fileSystem.GetDirectory("Renamed").InspectAsync(timeout.Token)).Exists);
                string payload = fileSystem.GetFile("Docs/left/nested/pending.bin").FullPath;
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), timeout.Token));
                Assert.AreEqual(0, source.Reads);
            }
            TestContext.WriteLine($"RootLabelSubtreePreparation: state={root.State}; originalProof=True; originalStoreAuthenticated=True; fullBindingMember=True; protectedAnchor=True; entireRegisteredTree=True; unregisteredRefused=True; clientReturnDiscarded=True; owners=3; noNativeStart=True; unmoved=True; stableIDs=True; ordinaryDeleteDenied=True; sourceReads=0; hostIntegrated=False.");
        }
        catch (Exception error) { TestContext.WriteLine("RootLabelSubtreePreparationFailure: " + error); throw; }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreRegisteredProtectionFixtureForCleanup(paths.SyncRootPath, caller.User!);
            RestoreDirectoryMovePayloadForCleanup(paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
