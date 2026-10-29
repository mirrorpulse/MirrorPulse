using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePublicCancellationRecoversAnInheritedBornRootThroughItsOriginalPurposeAfterRoleDisposal()
    {
        foreach (bool disabled in new[] { false, true })
            await ProbeBornRootCancellationAsync(disabled);
    }

    private async Task ProbeBornRootCancellationAsync(bool disabled)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.born-root-cancellation"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var source = new ProtectedPermissionNoSourceProvider(); var ranges = new RootLabelExecutionRejectedRanges();
        byte[] bytes = [23, 29, 31, 37, 41, 43];
        MirrorPulseRootRenameHistory original = null!;
        MirrorPulseNamespaceMemberRegistration bornRoot = null!, leaf = null!;
        var first = new Dictionary<Guid, MirrorPulseNamespaceBirthObservation>();
        string priorRole = string.Empty;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            await using (var role = new MirrorPulseNamespaceExecutionSession())
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token))
            await using (var actor = new MirrorPulseNamespaceActor())
            await using (var scheduler = new MirrorPulseInstanceScheduler())
            await using (var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog))
            {
                priorRole = role.RoleSid.Value;
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, role, deadline.Token);
                SetProtectedFixtureUserOwner(fileSystem.Root, caller.User!);
                // Capture only the empty physical anchor. The configured root must be
                // a real inherited remote birth, never an original capture fixture.
                await using var initialized = await new MirrorPulseNamespaceInitializationCoordinator(fileSystem, catalog,
                    new(paths.SyncRootPath, []), role, actor, scheduler, permissions).InitializeAsync(Guid.NewGuid(), deadline.Token);
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                bornRoot = await new MirrorPulseNamespaceRemoteCreationCoordinator(fileSystem, catalog, router, role, actor)
                    .CreateAsync(Guid.NewGuid(), initialized.Completion.AnchorMemberId, router.CreateRootPage().Children.Single(), deadline.Token);
                var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
                var left = await creation.CreateAsync(Guid.NewGuid(), bornRoot.MemberId, "left", true, deadline.Token);
                var nested = await creation.CreateAsync(Guid.NewGuid(), left.MemberId, "nested", true, deadline.Token);
                Guid import = Guid.NewGuid(); string inputPath = Path.Combine(directory, "input.bin");
                await File.WriteAllBytesAsync(inputPath, bytes, deadline.Token);
                var inputs = new MirrorPulseLocalImportStagingStore(paths);
                var content = await role.RunNormalUserOperationAsync(() => inputs.StageAsync(import, inputPath, deadline.Token));
                await using (var input = await role.RunNormalUserOperationAsync(() => inputs.OpenLeaseAsync(import, content, deadline.Token)))
                {
                    _ = await creation.PrepareImportAsync(import, nested.MemberId, "pending.bin", input, deadline.Token);
                    leaf = await creation.ExecuteImportAsync(import, input, deadline.Token);
                }
                foreach (var member in new[] { bornRoot, left, nested, leaf })
                    first.Add(member.MemberId, (await catalog.ReadNamespaceBirthObservationAsync(member.OriginId, deadline.Token))!);
                Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, bornRoot.Origin);
                Assert.IsEmpty(await catalog.ReadNamespacePermissionTreesAsync(root.RootId, cancellationToken: deadline.Token));
                Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
                if (disabled)
                {
                    await catalog.SetInstanceEnabledAsync(root.InstanceId, false, deadline.Token);
                    root = (await catalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
                    router.ReplaceRegistrations([root], await catalog.ReadManagedRootNamesAsync(deadline.Token));
                }
                await role.RunNamespaceOperationAsync(async () => original = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                    .PrepareAsync(root.RootId, "Renamed", deadline.Token));
                Assert.IsNull(await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(original.Intent.OperationId, deadline.Token));
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(original.Intent.OperationId, deadline.Token));
            }

            await using var currentRole = new MirrorPulseNamespaceExecutionSession();
            Assert.AreNotEqual(priorRole, currentRole.RoleSid.Value);
            await using var currentCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            await using var currentActor = new MirrorPulseNamespaceActor(); await using var currentScheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var demand = new MirrorPulseDemandProvider(router, ranges, namespaceWork: work, catalog: currentCatalog);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: demand, router: router, catalog: currentCatalog,
                namespaceExecution: currentRole, namespaceActor: currentActor, scheduler: currentScheduler, namespaceWork: work, initializeNamespaceOnStart: true);
            await using var runtime = factory.Create(paths); await runtime.StartAsync(deadline.Token);
            Assert.IsFalse(runtime.NamespaceStartupResults.Single(item => item.RootId == root.RootId).Admitted);
            Assert.IsFalse(work.IsAdmitted(root.RootId));
            var before = await currentCatalog.ReadRootLabelCancellationTreeAsync(original.Intent.OperationId, deadline.Token);
            Assert.IsNull(before.RecoveryOwner);
            var retainedRoot = before.Members.Single(member => member.Context.Member.MemberId == bornRoot.MemberId);
            string actual = new DirectoryInfo(Path.Combine(paths.SyncRootPath, "Docs")).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var difference = MirrorPulseRootLabelCancellationObjectInspection.Observe(MirrorPulseCancellationObjectScope.Root,
                MirrorPulseCancellationObjectOrigin.ProtectedBirth, retainedRoot.Context.Verification.Dacl, actual, currentRole.RoleSid.Value);
            Assert.AreEqual(MirrorPulseCancellationDescriptorDifference.Principals, difference.Difference);
            Assert.AreEqual(MirrorPulseCancellationRoleReference.Present, difference.CurrentExecutionRoleReference);
            Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Anchor.Context.Verification.Dacl, true), actual));

            var cancelled = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, cancelled.Intent.Phase);
            Assert.AreEqual("Docs", cancelled.CurrentDirectoryName); Assert.AreEqual(4, cancelled.MemberCount);
            Assert.IsTrue(cancelled.CurrentTreeAudited); Assert.AreEqual(!disabled, cancelled.NamespaceReady);
            var receipt = await currentCatalog.ReadUnsealedRootLabelCancellationAsync(original.Intent.OperationId, deadline.Token);
            Assert.IsNotNull(receipt); Assert.IsNotNull(receipt.RecoveryOwner);
            var recovered = await currentCatalog.ReadNamespaceInitializationStatusAsync(receipt.RecoveryOwner.InitializationOperationId, deadline.Token);
            Assert.AreEqual(5, recovered.Intent.Version); Assert.IsNotNull(recovered.Intent.RootLabelCancellation);
            Assert.AreEqual(original.Intent.OperationId, recovered.Intent.RootLabelCancellation.RootLabelOperationId);
            Assert.AreEqual(bornRoot.MemberId, recovered.Intent.RootLabelCancellation.RootMemberId);
            Assert.IsNotNull(recovered.Completion); Assert.AreEqual(currentRole.RoleSid.Value, recovered.Completion.RoleSid);
            Assert.AreEqual(root.RootId, recovered.Completion.ScopeRoot);
            Assert.IsEmpty(await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
            Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveAsync(original.Intent.OperationId, deadline.Token));
            Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveStartAsync(original.Intent.OperationId, deadline.Token));
            foreach (Guid memberId in first.Keys)
            {
                var actions = await currentCatalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, deadline.Token);
                Assert.HasCount(1, actions, "The same initializer must retain its completed current-role born recovery instead of creating another action.");
                var action = actions[0];
                Assert.AreEqual(receipt.RecoveryOwner.InitializationOperationId, action.InitializationOperationId);
                Assert.AreEqual(new MirrorPulseNamespaceBornRootLabelRecoveryOwner(original.Intent.OperationId, receipt.RecoveryOwner), action.RootLabelRecoveryOwner);
                var completed = await currentCatalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId, deadline.Token);
                Assert.IsNotNull(completed); Assert.IsTrue(completed.Drained);
                var protection = await currentCatalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId, deadline.Token);
                Assert.IsNotNull(protection); Assert.AreEqual(completed.ProtectionId, protection.ProtectionId);
                Assert.AreEqual(currentRole.RoleSid.Value, protection.RoleSid);
            }
            TestContext.WriteLine($"PublicBornRootCancellationRetention: disabled={disabled}; completedActions=4; repeatedBornRecoveryActions=0; sameInitializer=True; currentRole=True; finalFreshNativeAudit=True; readinessAfterAuditOnly=True.");
            var replay = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(cancelled.Intent, replay.Intent); Assert.AreEqual(cancelled.CancelledAt, replay.CancelledAt);
            var later = await runtime.PrepareManagedRootRenameAsync(root.RootId, "FinalLabel", deadline.Token);
            _ = await runtime.ExecuteManagedRootRenameAsync(later.Intent.OperationId, deadline.Token);
            replay = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual("FinalLabel", replay.CurrentDirectoryName); Assert.AreEqual(cancelled.CancelledAt, replay.CancelledAt);
            Assert.IsTrue(replay.CurrentTreeAudited); Assert.AreEqual(!disabled, replay.NamespaceReady);
            Assert.AreEqual(original.Proof, (await currentCatalog.ReadManagedRootRenameHistoryAsync(original.Intent.OperationId, deadline.Token))!.Proof);
            foreach (var entry in first)
            {
                var member = (await currentCatalog.ReadNamespaceMemberAsync(entry.Key, deadline.Token))!;
                Assert.AreEqual(entry.Value, await currentCatalog.ReadNamespaceBirthObservationAsync(member.OriginId, deadline.Token));
            }
            Assert.AreEqual(bornRoot.MemberId, (await runtime.InspectNamespaceMemberAsync("FinalLabel", true, deadline.Token)).MemberId);
            Assert.AreEqual(leaf.MemberId, (await runtime.InspectNamespaceMemberAsync("FinalLabel/left/nested/pending.bin", false, deadline.Token)).MemberId);
            string payload = Path.Combine(paths.SyncRootPath, "FinalLabel", "left", "nested", "pending.bin");
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
            Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, ranges.Calls);
            TestContext.WriteLine($"PublicBornRootCancellationActual: disabled={disabled}; originalProtectedBirth=True; independentRoleDisposed=True; " +
                $"originalPreparedWithoutInitializer=True; inheritedPrincipalTransitionVerified=True; exactPersistentCancellationPurpose=True; " +
                $"actualPublicNotMoved=True; completeMembers=4; noMoveAdmission=True; sameInitializerFreshlyCompleted=True; " +
                $"firstObservationsAndBindingsPreserved=True; latestLabelAndBytesPreserved=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!, additionalRootName: "FinalLabel");
            DeleteFixture(directory);
        }
    }
}
