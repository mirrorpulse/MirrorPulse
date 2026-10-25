using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseManagedRootNamespaceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners()
    {
        await ProbeRootRenamePreparationCutsAsync(RootRegistrationState.Active);
        await ProbeRootRenamePreparationCutsAsync(RootRegistrationState.Disabled);
    }

    private async Task ProbeRootRenamePreparationCutsAsync(RootRegistrationState availability)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rename-cuts"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), availability,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        CloudPlaceholderIdentity child = router.CreateFileIdentity(registration.InstanceId, "docs", "child", "v1");
        MirrorPulseRootRenameProof? unpublished = null;
        MirrorPulseRootRenameHistory? committed = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            for (int owner = 0; owner < 3; owner++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (owner == 0)
                    await SaveNamespaceTopologyAsync(catalog, registration, root, timeout.Token);
                var state = new MirrorPulseCfSharpStateSession(paths);
                var provider = new NamespaceProbeProvider();
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(provider).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                int captureCalls = 0;
                int commitCalls = 0;
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                {
                    PreparationCheckpoint = async (checkpoint, proof, token) =>
                    {
                        if (checkpoint == MirrorPulseRootRenamePreparationCheckpoint.AfterPublicProofCaptured)
                        {
                            captureCalls++;
                            Assert.IsEmpty(await catalog.ReadManagedRootRenameHistoryAsync(token));
                            Assert.IsEmpty(await catalog.ReadManagedRootRenamesAsync(token));
                            if (owner == 0)
                            {
                                unpublished = proof;
                                throw new IOException("Injected interruption after public SDK capture.");
                            }
                            Assert.AreEqual(1, owner, "A witnessed retry must not capture another SDK preparation.");
                            Assert.AreNotEqual(DecodePreparationProof(unpublished!).ProofId, DecodePreparationProof(proof).ProofId);
                            Assert.AreEqual(unpublished!.ItemId, proof.ItemId);
                            Assert.AreEqual(unpublished.LocalObject, proof.LocalObject);
                            Assert.AreEqual(unpublished.PlaceholderIdentity, proof.PlaceholderIdentity);
                            return;
                        }
                        commitCalls++;
                        Assert.AreEqual(1, owner);
                        committed = (await catalog.ReadManagedRootRenameHistoryAsync(token)).Single();
                        Assert.AreEqual(proof, committed.Proof);
                        Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, committed.Intent.Phase);
                        Assert.IsNull(committed.Observation);
                        Assert.AreEqual(committed.Intent, (await catalog.ReadManagedRootRenamesAsync(token)).Single());
                        throw new IOException("Injected interruption after atomic product commit.");
                    }
                };
                if (owner == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("child.txt", child, 4).WithInSyncState(true)
                            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                }
                else
                {
                    // The first SDK preparation remains authenticated after both owners close,
                    // including when a later preparation updates the library's source/target index.
                    CloudDirectoryMoveReconciliationResult original = await fileSystem.GetDirectory("Docs")
                        .ReconcileMoveAsync(DecodePreparationProof(unpublished!), timeout.Token);
                    Assert.AreEqual(CloudDirectoryMoveReconciliationOutcome.NotMoved, original.Outcome);
                    Assert.IsFalse(original.NativeMoveObserved);
                    Assert.IsFalse(original.RequiresFullRescan);
                }
                if (owner < 2)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.PrepareAsync(registration.RootId, "Renamed", timeout.Token).AsTask());
                    Assert.AreEqual(1, captureCalls);
                    Assert.AreEqual(owner, commitCalls);
                    Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single().DirectoryName);
                    Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs")));
                    Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Renamed")));
                    continue;
                }
                MirrorPulseRootRenameHistory retried = await coordinator.PrepareAsync(registration.RootId, "Renamed", timeout.Token);
                Assert.AreEqual(committed, retried);
                Assert.AreEqual(0, captureCalls);
                Assert.AreEqual(0, commitCalls);
                Assert.IsTrue((await RunNamespaceProcessAsync(root, "rename", timeout.Token)).Completed);
                Assert.IsGreaterThan(0, provider.RenameApprovals);
                MirrorPulseManagedRootRenameRecovery recovered = await coordinator.RecoverAsync(retried.Intent.OperationId, timeout.Token);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, recovered.Intent.Phase,
                    $"Recovery outcome={recovered.LibraryResult?.Outcome}; stage={recovered.LibraryResult?.Stage}; nativeHResult={recovered.LibraryResult?.NativeHResult:X8}.");
                Assert.IsTrue(recovered.LibraryResult!.NativeMoveObserved);
                Assert.IsTrue(recovered.LibraryResult.DurableProjectionCommitted);
                Assert.IsFalse(recovered.LibraryResult.RequiresFullRescan);
                RootRegistration renamed = (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single();
                Assert.AreEqual(registration.RootId, renamed.RootId);
                Assert.AreEqual(registration.InstanceId, renamed.InstanceId);
                Assert.AreEqual(registration.UniquenessKey, renamed.UniquenessKey);
                Assert.AreEqual(availability, renamed.State);
                Assert.AreEqual("Renamed", renamed.Label);
                Assert.AreEqual("Renamed", renamed.DirectoryName);
                MirrorPulseRootRenameHistory receipt = (await catalog.ReadManagedRootRenameHistoryAsync(timeout.Token)).Single();
                Assert.IsNotNull(receipt.Proof);
                Assert.AreEqual(committed!.Proof, receipt.Proof);
                Assert.AreEqual(committed.Proof!.LocalObject, receipt.Observation!.LocalObject);
                Assert.AreEqual(committed.Proof.PlaceholderIdentity, receipt.Observation.PlaceholderIdentity);
                await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                Assert.AreEqual(receipt.Proof.ItemId, (await transaction.Items.GetByRelativePathAsync("Renamed", timeout.Token))!.ItemId);
                Assert.AreEqual(child.ItemId, (await transaction.Items.GetByRelativePathAsync(Path.Combine("Renamed", "child.txt"), timeout.Token))!.ItemId);
                Assert.IsNull(await transaction.Items.GetByRelativePathAsync("Docs", timeout.Token));
                Assert.IsNull(await transaction.Items.GetByRelativePathAsync(Path.Combine("Docs", "child.txt"), timeout.Token));
                await transaction.RollbackAsync(timeout.Token);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Renamed", "child.txt")));
                Assert.AreEqual("docs", router.ResolveCurrentPath(Path.Combine("Renamed", "child.txt")).RootKey);
                TestContext.WriteLine($"RootRenamePreparationCuts: availability={availability}; SDKCaptureOnlyLeavesNoProductIntent=True; completeProofSurvivesLostReturn=True; retryDoesNotRecapture=True; earlierSdkProofRetained=True; owners=3; stableRoot=True; stableChild=True; sourceCalls=0.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static CloudDirectoryMoveProof DecodePreparationProof(MirrorPulseRootRenameProof proof) =>
        CloudDirectoryMoveProof.Decode(Convert.FromBase64String(proof.DirectoryMoveEvidence!));
}
