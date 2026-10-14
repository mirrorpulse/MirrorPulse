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
    public async Task NativeSourcePopulationRegistersProtectedRemoteBirthBeforeDirectoryCallbackReturns()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.registered-remote-population"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Guid operation = Guid.NewGuid();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution)
            { DirectoryPage = new([new("cold", "v1", "file", "cold.bin", 4, null, null, false)], default, true) };
            var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var delegated = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source));
            CloudFileSystem current = null!;
            MirrorPulseNamespaceRemoteCreationCoordinator creator = null!;
            Guid parentMember = Guid.Empty;
            MirrorPulseNamespaceMemberRegistration? published = null;
            var identity = router.CreateFileIdentity(registration.InstanceId, registration.UniquenessKey, "cold", "v1");
            var provider = new ProactivePopulationProvider(delegated, router, () => current, execution, actor, identity,
                async (specification, token) => { published = await creator.CreateAsync(operation, parentMember, specification, token); });
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
            current = fileSystem; creator = new(fileSystem, catalog, router, execution, actor);
            await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, timeout.Token);
            await using var feed = fileSystem.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
            await execution.RunNamespaceOperationAsync(async () =>
                await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
            SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!); SetProtectedFixtureUserOwner(fileSystem.GetDirectory("Docs"), caller.User!);
            await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, execution,
                actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), timeout.Token);
            parentMember = (await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, timeout.Token))
                .Single(item => item.RelativePath == "Docs").MemberId;
            var enumerated = await RunSourceConsumerAsync(directory, "enumerate", execution, timeout.Token);
            CollectionAssert.AreEqual(CallbackDrainExpectedChildren, enumerated.Names);
            Assert.IsNotNull(published); Assert.AreEqual(operation, published.MemberId);
            Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, published.Origin);
            var birth = await catalog.ReadNamespaceBirthAsync(operation, timeout.Token);
            Assert.IsNotNull(birth); Assert.AreEqual(MirrorPulseNamespaceBirthOrigin.RemotePopulation, birth.Origin);
            Assert.IsNotNull(await catalog.ReadNamespaceBirthStartAsync(operation, timeout.Token));
            var observed = await catalog.ReadNamespaceBirthObservationAsync(operation, timeout.Token);
            Assert.IsNotNull(observed); Assert.AreEqual(published.LocalObject, observed.LocalObject); Assert.AreEqual(identity.ItemId, observed.ItemId);
            Assert.IsNotNull(await catalog.ReadLatestNamespaceBirthProtectionAsync(operation, timeout.Token));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
            Assert.HasCount(3, await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, timeout.Token));
            var snapshot = await fileSystem.GetFile("Docs/cold.bin").InspectAsync(timeout.Token);
            Assert.AreEqual(identity, CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(Path.Combine(paths.SyncRootPath, "Docs", "cold.bin")), timeout.Token));
            var replay = await creator.CreateAsync(operation, parentMember,
                CloudFilePlaceholderSpec.CreateBuilder("cold.bin", identity, 4).WithInSyncState(true)
                    .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build(), timeout.Token);
            Assert.AreEqual(published, replay); Assert.AreEqual(observed, await catalog.ReadNamespaceBirthObservationAsync(operation, timeout.Token));
            Assert.AreEqual(1, provider.Creations); Assert.AreEqual(1, worker.Calls.Count(call => call == "directory"));
            Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
            var addedRoot = AdapterRootRegistrationMapper.Map(registration.AdapterId, registration.InstanceId,
                new AdapterRootDefinition("more", "More", "More", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            router.ReplaceRegistrations([registration, addedRoot]);
            var rootSpec = router.CreateRootPage().Children.Single(spec => spec.Name == "More");
            var bornRoot = await creator.CreateAsync(Guid.NewGuid(), initialized.Completion.AnchorMemberId, rootSpec, timeout.Token);
            Assert.AreEqual(addedRoot.RootId, bornRoot.RootId); Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, bornRoot.Origin);
            Assert.AreEqual(initialized.Completion.AnchorMemberId, bornRoot.ParentMemberId);
            Assert.IsEmpty(await catalog.ReadNamespacePermissionTreesAsync(addedRoot.RootId, cancellationToken: timeout.Token));
            var nestedIdentity = router.CreateFileIdentity(addedRoot.InstanceId, addedRoot.UniquenessKey, "nested", "v1");
            var nested = await creator.CreateAsync(Guid.NewGuid(), bornRoot.MemberId,
                CloudFilePlaceholderSpec.CreateBuilder("nested.bin", nestedIdentity, 4).WithInSyncState(true)
                    .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build(), timeout.Token);
            Assert.AreEqual(addedRoot.RootId, nested.RootId); Assert.AreEqual(bornRoot.MemberId, nested.ParentMemberId);
            Assert.HasCount(5, await catalog.ReadNamespaceMemberTreeAsync(initialized.Completion.AnchorMemberId, timeout.Token));
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => Directory.Delete(Path.Combine(paths.SyncRootPath, "More"), true), timeout.Token));
            Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "More")));
            Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "More", "nested.bin")));
            TestContext.WriteLine("RegisteredSourcePopulation: normalExternalEnumeration=True; sourceNormalUser=True; productionRemoteBirthCoordinator=True; beforeNativeIntentPlanStart=True; maturePublicCreationAndSqlite=True; actualFirstBinding=True; inheritedProtectionVerified=True; membershipBeforePageReturn=True; reservationReleasedWithMembership=True; replayPreservesFirstObservation=True; newTopRootProtectedBirth=True; newRootNoOriginalCapture=True; nestedBirthUsesActualNewRoot=True; noFakeOriginal=True; hydration=False; defaultHostStartup=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreCurrentRuntimeFixtureForCleanup(directory, paths.SyncRootPath, caller.User!); DeleteFixture(directory);
        }
    }
}
