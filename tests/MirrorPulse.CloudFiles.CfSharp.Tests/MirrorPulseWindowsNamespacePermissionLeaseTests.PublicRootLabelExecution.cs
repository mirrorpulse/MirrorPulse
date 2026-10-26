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
    public async Task NativePublicRuntimeExecutesOnlyOriginalProtectedRootLabelsAndAuditsCurrentReadmission()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (bool caseOnly in new[] { false, true })
                await ProbePublicRootLabelExecutionAsync(disabled, caseOnly);
    }

    private async Task ProbePublicRootLabelExecutionAsync(bool disabled, bool caseOnly)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.public-root-label"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        byte[] bytes = [3, 5, 7, 11, 13, 17];
        var source = new ProtectedPermissionNoSourceProvider();
        var ranges = new RootLabelExecutionRejectedRanges();
        string priorRole;
        Guid fileMemberId;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            // Prepare ordinary children under a different, fully disposed owner.
            // No Label intent or first-native start crosses this role boundary.
            await using (var role = new MirrorPulseNamespaceExecutionSession())
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token))
            await using (var actor = new MirrorPulseNamespaceActor())
            await using (var scheduler = new MirrorPulseInstanceScheduler())
            await using (var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog))
            {
                priorRole = role.RoleSid.Value;
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using (var feed = fileSystem.CreateLocalChangeFeed())
                {
                    await feed.StartAsync(deadline.Token);
                    await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, deadline.Token);
                }
                await File.WriteAllBytesAsync(fileSystem.GetFile("Docs/original.bin").FullPath, bytes, deadline.Token);
                foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetFile("Docs/original.bin") })
                    SetProtectedFixtureUserOwner(item, caller.User!);
                await fileSystem.GetDirectory("Docs").SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog, router, role,
                    actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token);
                var rootMember = (await catalog.ReadNamespaceMemberChildAsync(initialized.Completion.AnchorMemberId, "Docs", deadline.Token))!;
                var file = (await catalog.ReadNamespaceMemberChildAsync(rootMember.MemberId, "original.bin", deadline.Token))!;
                fileMemberId = file.MemberId;
                var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                var left = await creation.CreateAsync(Guid.NewGuid(), rootMember.MemberId, "left", true, deadline.Token);
                var nested = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "nested", true, deadline.Token);
                var moves = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                var move = await moves.PrepareAsync(Guid.NewGuid(), file.MemberId, nested.MemberId, "pending.bin", deadline.Token);
                await moves.ExecuteAsync(move.OperationId, deadline.Token);
                if (disabled)
                {
                    await catalog.SetInstanceEnabledAsync(root.InstanceId, false, deadline.Token);
                    root = (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
                    router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(deadline.Token));
                }
            }

            await using var currentRole = new MirrorPulseNamespaceExecutionSession();
            Assert.AreNotEqual(priorRole, currentRole.RoleSid.Value);
            await using var currentCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            await using var currentActor = new MirrorPulseNamespaceActor();
            await using var currentScheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var demand = new MirrorPulseDemandProvider(router, ranges, namespaceWork: work, catalog: currentCatalog);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: demand, router: router, catalog: currentCatalog,
                namespaceExecution: currentRole, namespaceActor: currentActor, scheduler: currentScheduler, namespaceWork: work, initializeNamespaceOnStart: true);
            await using var runtime = factory.Create(paths);
            await runtime.StartAsync(deadline.Token);
            Assert.IsTrue(runtime.NamespaceStartupResults.Single(item => item.RootId == root.RootId).Admitted);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.ExecuteManagedRootRenameAsync(Guid.Empty, deadline.Token).AsTask());
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => runtime.ExecuteManagedRootRenameAsync(Guid.NewGuid(), deadline.Token).AsTask());
            string target = caseOnly ? "docs" : "Renamed";
            MirrorPulseRootRenameHistory original;
            // Exercise actual public preparation draining accepted work before
            // it can capture proof or obtain instance/library serialization.
            using (var held = work.Enter([root.RootId]))
            {
                Task<MirrorPulseRootRenameHistory> preparing = runtime.PrepareManagedRootRenameAsync(root.RootId, target, deadline.Token).AsTask();
                while (work.IsAdmitted(root.RootId)) await Task.Delay(10, deadline.Token);
                Assert.IsFalse(preparing.IsCompleted);
                held.Dispose();
                original = await preparing;
            }
            Assert.IsFalse(work.IsAdmitted(root.RootId));
            Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveStartAsync(original.Intent.OperationId, deadline.Token));
            var first = await runtime.ExecuteManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, first.Intent.Phase);
            Assert.AreEqual(original.Intent.OperationId, first.Intent.OperationId);
            Assert.AreEqual(target, first.CurrentDirectoryName);
            Assert.AreEqual(4, first.MemberCount);
            Assert.IsTrue(first.CurrentTreeAudited);
            Assert.AreEqual(!disabled, first.NamespaceReady);
            Assert.AreEqual(!disabled, work.IsAdmitted(root.RootId));
            var intent = (await currentCatalog.ReadNamespaceMemberMoveAsync(original.Intent.OperationId, deadline.Token))!;
            Assert.AreEqual(currentRole.RoleSid.Value, intent.RoleSid);
            var tree = (await currentCatalog.ReadNamespaceDirectoryMoveCompletionAsync(original.Intent.OperationId, deadline.Token))!;
            Assert.HasCount(4, tree.Entries);
            var firstHistory = (await currentCatalog.ReadManagedRootRenameHistoryAsync(original.Intent.OperationId, deadline.Token))!;
            Assert.AreEqual(original.Proof, firstHistory.Proof);
            Assert.AreEqual(fileMemberId, (await runtime.InspectNamespaceMemberAsync(target + "/left/nested/pending.bin", false, deadline.Token)).MemberId);
            Assert.AreEqual(target, Directory.EnumerateDirectories(paths.SyncRootPath).Select(Path.GetFileName).Single());
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, target, "left", "nested", "pending.bin"), deadline.Token));

            // A later Label changes today's name. Retrying an earlier completed
            // command must return its original receipt without reverting it.
            var later = await runtime.PrepareManagedRootRenameAsync(root.RootId, "FinalLabel", deadline.Token);
            _ = await runtime.ExecuteManagedRootRenameAsync(later.Intent.OperationId, deadline.Token);
            var replay = await runtime.ExecuteManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(first.Intent, replay.Intent);
            Assert.AreEqual("FinalLabel", replay.CurrentDirectoryName);
            Assert.IsTrue(replay.CurrentTreeAudited);
            Assert.AreEqual(!disabled, replay.NamespaceReady);
            Assert.AreEqual(firstHistory, await currentCatalog.ReadManagedRootRenameHistoryAsync(original.Intent.OperationId, deadline.Token));
            Assert.AreEqual("FinalLabel", router.FindRegistration(root.RootId)!.DirectoryName);
            Assert.AreEqual(root.State, router.FindRegistration(root.RootId)!.State);
            Assert.AreEqual(fileMemberId, (await runtime.InspectNamespaceMemberAsync("FinalLabel/left/nested/pending.bin", false, deadline.Token)).MemberId);
            string payload = Path.Combine(paths.SyncRootPath, "FinalLabel", "left", "nested", "pending.bin");
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
            Assert.IsNull(await currentCatalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
            Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, ranges.Calls);
            TestContext.WriteLine($"PublicRootLabelExecutionActual: disabled={disabled}; caseOnly={caseOnly}; originalOperation=True; currentRoleFirstNative=True; preparationDrainsWork=True; completeMembers=4; freshCurrentAudit=True; activeReady={!disabled}; immutableHistoricalReplay=True; latestLabelPreserved=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }
}
