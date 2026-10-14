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
    public async Task NativePublicCreationInsideDirectoryCallbackEnumeratesTheCreatedPlaceholder()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.proactive-population"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution)
            { DirectoryPage = new([new("cold", "v1", "file", "cold.bin", 4, null, null, false)], default, true) };
            var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var delegated = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source));
            CloudFileSystem current = null!;
            var identity = router.CreateFileIdentity(registration.InstanceId, registration.UniquenessKey, "cold", "v1");
            var provider = new ProactivePopulationProvider(delegated, router, () => current, execution, actor, identity);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
            current = fileSystem; await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, timeout.Token);
            await using var feed = fileSystem.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
            await execution.RunNamespaceOperationAsync(async () =>
                await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
            SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!); SetProtectedFixtureUserOwner(fileSystem.GetDirectory("Docs"), caller.User!);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await using var anchor = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, timeout.Token);
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            foreach (var item in new[] { fileSystem.Root, fileSystem.GetDirectory("Docs") })
            {
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, timeout.Token);
                var actual = await guard.InspectAsync(timeout.Token);
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), actual.RootId, actual.LocalObject,
                    actual.RelativePath, true, actual.OwnerSid, actual.Dacl, actual.ObservedAt);
                originals.Add(new(baseline, new(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject, baseline.RootId,
                    baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, execution.RoleSid.Value,
                    baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, true), DateTimeOffset.UtcNow)));
            }
            await catalog.PrepareNamespacePermissionChangesAsync(originals, timeout.Token);
            await execution.RunNamespaceOperationAsync(async () =>
            {
                for (int index = originals.Count - 1; index >= 0; index--)
                {
                    var applied = await permissions.ApplyProtectedAsync(originals[index].Intent.OperationId,
                        index == 0 ? fileSystem.Root : fileSystem.GetDirectory("Docs"), router, cancellationToken: timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
                    AssertCompletedRegisteredProtectionScope(applied.Receipt!);
                }
            });
            var enumerated = await RunSourceConsumerAsync(directory, "enumerate", execution, timeout.Token);
            CollectionAssert.AreEqual(CallbackDrainExpectedChildren, enumerated.Names);
            Assert.AreEqual(1, provider.Creations); Assert.AreEqual(1, worker.Calls.Count(call => call == "directory"));
            var actualFile = await fileSystem.GetFile("Docs/cold.bin").InspectAsync(timeout.Token);
            Assert.IsTrue(actualFile.IsPlaceholder); Assert.AreEqual(identity, CloudPlaceholderIdentity.Decode(actualFile.PlaceholderIdentity.Span));
            await using (var transaction = await state.OpenStore.BeginTransactionAsync(timeout.Token))
            {
                var projected = await transaction.Items.GetByItemIdAsync(identity.ItemId, timeout.Token);
                Assert.IsNotNull(projected); Assert.AreEqual(identity.RemoteId, projected.RemoteId); Assert.AreEqual("v1", projected.RemoteRevision);
                await transaction.RollbackAsync(timeout.Token);
            }
            Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
            TestContext.WriteLine("ProactiveCallbackPopulation: ordinaryExternalEnumeration=True; sourceNormalUser=True; sharedActor=True; protectedDirectories=True; publicCreatePlaceholdersBeforeCallbackReturn=True; returnedPageEmptyAndComplete=True; publicNativeIdentityAndOfficialProjection=True; hydration=False; productBirthRegistration=False; defaultHostStartup=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }

    private sealed class ProactivePopulationProvider(MirrorPulseDemandProvider delegated, MirrorPulseRootRouter router,
        Func<CloudFileSystem> fileSystem, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
        CloudPlaceholderIdentity identity, Func<CloudPlaceholderSpec, CancellationToken, ValueTask>? publish = null) : ICloudDemandProvider
    {
        public int Creations { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) => delegated.OpenReadAsync(request, cancellationToken);
        public async ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(CloudProviderFetchPlaceholdersRequest request, CancellationToken cancellationToken)
        {
            var page = await delegated.FetchChildrenAsync(request, cancellationToken);
            if (router.IsSyncRoot(request.NormalizedPath)) return page;
            Assert.HasCount(1, page.Children);
            if (publish is not null)
            {
                await publish(page.Children[0], cancellationToken);
                Creations++; return new CloudProviderDirectoryPage([]);
            }
            await actor.RunAsync(async token =>
            {
                actor.AssertAccess();
                await execution.RunNamespaceOperationAsync(async () =>
                    await fileSystem().GetDirectory("Docs").CreatePlaceholdersAsync([
                        CloudFilePlaceholderSpec.CreateBuilder("cold.bin", identity, 4).WithInSyncState(true)
                            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: token));
                Creations++; return true;
            }, cancellationToken);
            return new CloudProviderDirectoryPage([]);
        }
    }
}
