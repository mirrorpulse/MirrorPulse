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
    public async Task NativeOriginalRemoteBirthRegistersItsFirstObjectBeforeCursorAndReopensObservedDirectory()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.original-batch-birth"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new RemoteMovementContentGuard();
        var directoryIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "new-directory", "v1");
        var fileIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "new-file", "v1");
        var batch = new CloudRemoteChangeBatch(root.InstanceId + "/birth", new byte[] { 9 },
            [new("directory", CloudRemoteChangeKind.DirectoryUpsert, directoryIdentity.RemoteId, "v1", CloudItemKind.Directory,
                "Docs\\born", directoryIdentity.ItemId, metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()),
             new("file", CloudRemoteChangeKind.FileUpsert, fileIdentity.RemoteId, "v1", CloudItemKind.File,
                "Docs\\born\\cold.bin", fileIdentity.ItemId, length: 4, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: new byte[] { 2 })],
            new byte[] { 2 });
        MirrorPulseNamespaceBirthIntent original = null!;
        MirrorPulseNamespaceBirthObservation first = null!;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(provider).Build())
            {
                await setup.StartAsync(deadline.Token);
                (await setup.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: deadline.Token)).ThrowIfAnyFailed();
                SetProtectedFixtureUserOwner(setup.Root, caller.User!);
                SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), caller.User!);
            }
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            for (int owner = 0; owner < 2; owner++)
            {
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, deadline.Token);
                await using var actor = new MirrorPulseNamespaceActor(); await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                await using var initialized = owner == 0 ? await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    router, execution, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token) : null;
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                var driver = new MirrorPulseRemoteNamespaceBatchDriver(fileSystem, state, catalog, router, execution, actor,
                    birthCheckpoint: owner == 0 ? (point, _) =>
                        point == MirrorPulseRemoteBirthCheckpoint.AfterObservation
                            ? ValueTask.FromException(new IOException("Synthetic interruption after original first observation."))
                            : ValueTask.CompletedTask : null);
                var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, null, driver);
                if (owner == 0)
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(root.InstanceId, batch,
                        cancellationToken: deadline.Token).AsTask());
                    original = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, batch.BatchId, 0, deadline.Token))!;
                    first = (await catalog.ReadNamespaceBirthObservationAsync(original.OperationId, deadline.Token))!;
                    Assert.IsNull(await catalog.ReadNamespaceMemberAsync(original.OperationId, deadline.Token));
                    Assert.IsNull(await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token));
                    Assert.IsTrue(await catalog.CanResumeNamespaceRemoteBirthAsync(original, deadline.Token));
                    Assert.IsTrue(await new MirrorPulseRemoteNamespaceFence(router, catalog).CanReplayAsync(root, batch, deadline.Token));
                }
                else
                {
                    var result = await coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status); Assert.IsFalse(result.RequiresRetry);
                    Assert.AreEqual(original, await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, batch.BatchId, 0, deadline.Token));
                    Assert.AreEqual(first, await catalog.ReadNamespaceBirthObservationAsync(original.OperationId, deadline.Token));
                    var directoryMember = (await catalog.ReadNamespaceMemberAsync(original.OperationId, deadline.Token))!;
                    Assert.AreEqual(first.LocalObject, directoryMember.LocalObject);
                    var child = (await catalog.ReadNamespaceRemoteBirthAsync(root.InstanceId, batch.BatchId, 1, deadline.Token))!;
                    Assert.AreEqual(directoryMember.MemberId, child.RemoteEntry!.ParentMemberId);
                    Assert.IsNotNull(await catalog.ReadNamespaceMemberAsync(child.OperationId, deadline.Token));
                    Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                    CollectionAssert.AreEqual(batch.FinalCursor.ToArray(), (await coordinator.ReadCursorAsync(root.InstanceId, deadline.Token))!.Value.ToArray());
                    var replay = await coordinator.ApplyAsync(root.InstanceId, batch, cancellationToken: deadline.Token);
                    Assert.AreEqual(CloudRemoteBatchStatus.Applied, replay.Status);
                    Assert.AreEqual(first.LocalObject.LocalFileId, (await fileSystem.GetDirectory("Docs/born").InspectAsync(deadline.Token)).LocalBinding!.LocalFileId);
                    Assert.AreEqual(0, provider.Reads);
                }
            }
            TestContext.WriteLine("OriginalRemoteBirth: unchangedSdkBatch=True; sdkOwnsNativeCreation=True; noPrecreation=True; " +
                "firstObservedDirectoryReopened=True; nestedFileUsesRegisteredParent=True; twoProductAndSdkOwners=True; " +
                "memberBeforeAdapterCursor=True; originalBindingRetained=True; sameRole=True; hydration=False; " +
                "unobservedStartRecovery=False; conflictClosure=False; defaultHostIntegrated=False.");
        }
        finally
        {
            try { await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, caller.User!, ["Docs", "Docs/born"], ["Docs/born/cold.bin"]); }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }
}
