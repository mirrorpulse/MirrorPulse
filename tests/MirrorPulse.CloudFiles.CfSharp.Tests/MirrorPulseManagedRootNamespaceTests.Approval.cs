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
    public async Task NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners()
    {
        await ProbeDefaultRootRenameApprovalAsync(RootRegistrationState.Active);
        await ProbeDefaultRootRenameApprovalAsync(RootRegistrationState.Disabled);
    }

    private async Task ProbeDefaultRootRenameApprovalAsync(RootRegistrationState availability)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.rename-approval"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), availability, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var ranges = new RejectedRenameProbeRanges();
        CloudPlaceholderIdentity child = router.CreateFileIdentity(registration.InstanceId, "docs", "child", "v1");
        MirrorPulseRootRenameHistory? prepared = null;
        MirrorPulseRootRenameHistory? cancelled = null;
        CloudProviderRenameRequest? actualRequest = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                if (owner == 0) await SaveNamespaceTopologyAsync(catalog, registration, root, timeout.Token);
                var state = new MirrorPulseCfSharpStateSession(paths);
                var official = new MirrorPulseDemandProvider(router, ranges, catalog: catalog);
                var probe = new DefaultRenameApprovalProbe(official);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(probe).Build();
                await fileSystem.StartAsync(timeout.Token);
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router);
                if (owner == 0)
                {
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token);
                    await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("child.txt", child, 4).WithInSyncState(true)
                            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                    Assert.IsFalse((await RunNamespaceProcessAsync(root, "rename", timeout.Token)).Completed);
                    Assert.IsGreaterThan(0, probe.Denied, "The real callback did not reach the default product provider.");
                    Assert.AreEqual(0, probe.Allowed);
                    Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs")));
                    var other = await coordinator.PrepareAsync(registration.RootId, "Other", timeout.Token);
                    int before = probe.Denied;
                    Assert.IsFalse((await RunNamespaceProcessAsync(root, "rename", timeout.Token)).Completed);
                    Assert.IsGreaterThan(before, probe.Denied, "A different prepared target must remain denied by the real callback.");
                    await catalog.TransitionManagedRootRenameAsync(other.Intent.OperationId, MirrorPulseRootRenamePhase.Prepared,
                        MirrorPulseRootRenamePhase.Cancelled, timeout.Token);
                    cancelled = (await catalog.ReadManagedRootRenameHistoryAsync(other.Intent.OperationId, timeout.Token))!;
                    prepared = await coordinator.PrepareAsync(registration.RootId, "Renamed", timeout.Token);
                    Assert.IsNotNull(prepared.Proof);
                    var policy = new MirrorPulseManagedRootRenameApproval(router, catalog);
                    byte[] unrelated = MirrorPulsePlaceholderIdentity.Create(InstanceId.New(), "unrelated-root").ToCfSharp().Encode();
                    Assert.AreEqual(CloudProviderPolicyDecision.Deny, await policy.EvaluateAsync("Docs", unrelated, "Renamed", true, true, true, timeout.Token));
                    Assert.IsTrue((await RunNamespaceProcessAsync(root, "rename", timeout.Token)).Completed,
                        "The default product provider did not approve the exact witnessed root rename.");
                    Assert.AreEqual(1, probe.Allowed);
                    actualRequest = probe.LastAllowed;
                    Assert.IsNotNull(actualRequest);
                    Assert.IsTrue(actualRequest.IsDirectory && actualRequest.SourceInScope && actualRequest.TargetInScope);
                    Assert.AreEqual(prepared.Proof.PlaceholderIdentity, Convert.ToBase64String(actualRequest.FileIdentity.Span));
                    Assert.AreEqual(prepared, await catalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId, timeout.Token),
                        "Callback approval cannot advance a product phase or manufacture an observation.");
                    Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single().DirectoryName);
                    continue;
                }
                MirrorPulseManagedRootRenameRecovery recovery = await coordinator.RecoverAsync(prepared!.Intent.OperationId, timeout.Token);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, recovery.Intent.Phase,
                    $"Recovery outcome={recovery.LibraryResult?.Outcome}; stage={recovery.LibraryResult?.Stage}; nativeHResult={recovery.LibraryResult?.NativeHResult:X8}.");
                Assert.IsTrue(recovery.LibraryResult!.NativeMoveObserved && recovery.LibraryResult.DurableProjectionCommitted);
                Assert.IsFalse(recovery.LibraryResult.RequiresFullRescan);
                RootRegistration renamed = (await catalog.ReadAdapterTopologyAsync(timeout.Token)).Roots.Single();
                Assert.AreEqual(registration.RootId, renamed.RootId);
                Assert.AreEqual(registration.InstanceId, renamed.InstanceId);
                Assert.AreEqual(registration.UniquenessKey, renamed.UniquenessKey);
                Assert.AreEqual(availability, renamed.State);
                Assert.AreEqual("Renamed", renamed.Label);
                Assert.AreEqual("Renamed", renamed.DirectoryName);
                var receipt = (await catalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId, timeout.Token))!;
                Assert.AreEqual(prepared.Proof, receipt.Proof);
                Assert.AreEqual(prepared.Proof!.LocalObject, receipt.Observation!.LocalObject);
                Assert.AreEqual(prepared.Proof.PlaceholderIdentity, receipt.Observation.PlaceholderIdentity);
                Assert.AreEqual(cancelled, await catalog.ReadManagedRootRenameHistoryAsync(cancelled!.Intent.OperationId, timeout.Token));
                Assert.AreEqual(CloudProviderPolicyDecision.Deny, await official.ApproveRenameAsync(actualRequest!, timeout.Token),
                    "An old actual callback request must not authorize a completed historical operation.");
                await using ICloudStateTransaction transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token);
                Assert.AreEqual(receipt.Proof!.ItemId, (await transaction.Items.GetByRelativePathAsync("Renamed", timeout.Token))!.ItemId);
                Assert.AreEqual(child.ItemId, (await transaction.Items.GetByRelativePathAsync(Path.Combine("Renamed", "child.txt"), timeout.Token))!.ItemId);
                Assert.IsNull(await transaction.Items.GetByRelativePathAsync("Docs", timeout.Token));
                Assert.IsNull(await transaction.Items.GetByRelativePathAsync(Path.Combine("Docs", "child.txt"), timeout.Token));
                await transaction.RollbackAsync(timeout.Token);
                Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Renamed", "child.txt")));
                Assert.AreEqual(0, ranges.Calls);
                TestContext.WriteLine($"DefaultRootRenameApproval: availability={availability}; unpreparedDenied=True; wrongTargetDenied=True; unrelatedIdentityDenied=True; exactOriginalApproved=True; callbackLeavesProgressUntouched=True; oldCallbackDeniedAfterCompletion=True; cancelledProofRetained=True; stableRoot=True; stableChild=True; owners=2; sourceCalls=0.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RejectedRenameProbeRanges : IMirrorPulseWorkerRangeTransport
    {
        public int Calls { get; private set; }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("Root rename approval and original recovery must not read a source.");
        }
    }

    // Only demand enumeration is synthetic. Actual immutable native rename requests
    // are always passed to the production provider; there is no Allow override here.
    private sealed class DefaultRenameApprovalProbe(MirrorPulseDemandProvider provider) : ICloudDemandProvider
    {
        public int Denied { get; private set; }
        public int Allowed { get; private set; }
        public CloudProviderRenameRequest? LastAllowed { get; private set; }
        public async ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request, CancellationToken token)
        {
            var decision = await provider.ApproveRenameAsync(request, token);
            if (decision == CloudProviderPolicyDecision.Allow) { Allowed++; LastAllowed = request; }
            else Denied++;
            return decision;
        }
        public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(CloudProviderFetchPlaceholdersRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CloudProviderDirectoryPage([]));
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new AssertFailedException("The root rename approval probe must not hydrate content."));
    }
}
