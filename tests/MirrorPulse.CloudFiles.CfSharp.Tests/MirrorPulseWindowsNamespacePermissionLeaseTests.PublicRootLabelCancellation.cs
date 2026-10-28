using System.Security.AccessControl;
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
    public async Task NativePublicRuntimeCancelsOnlyUnstartedOriginalRootLabelsAcrossRolesAndRetainsLaterNames()
    {
        foreach (bool disabled in new[] { false, true })
            foreach (bool newRole in new[] { false, true })
                await ProbePublicRootLabelCancellationAsync(disabled, newRole);
        foreach (bool disabled in new[] { false, true })
            await ProbePublicRootLabelCancellationAsync(disabled, newRole: true, startWritten: true);
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePublicRuntimeCancelsOriginalUnsealedRootPreparationsAfterIndependentRoleDisposal()
    {
        foreach (bool disabled in new[] { false, true })
            await ProbePublicRootLabelCancellationAsync(disabled, newRole: true, unsealedBeforeRoleChange: true);
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativePublicCancellationRetainsAndResumesItsOriginalPartiallyRotatedInitializer()
    {
        foreach (bool disabled in new[] { false, true })
            await ProbePublicRootLabelCancellationAsync(disabled, newRole: true, unsealedBeforeRoleChange: true,
                retainedPartialInitializer: true);
        foreach (var phase in new[] { MirrorPulseNamespacePermissionPhase.Prepared, MirrorPulseNamespacePermissionPhase.Applied })
            foreach (bool disabled in new[] { false, true })
                await ProbePublicRootLabelCancellationAsync(disabled, newRole: true, unsealedBeforeRoleChange: true,
                    retainedPartialInitializer: true, pendingOriginalPhase: phase);
        foreach (var cut in new[] { MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterActionPrepared,
            MirrorPulseNamespaceBornMemberRecoveryCheckpoint.BeforeProtectedCallbackReturns, MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterScopeDrained,
            MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterProtectionReadback, MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterCompletionRecorded })
            foreach (bool disabled in new[] { false, true })
                await ProbePublicRootLabelCancellationAsync(disabled, newRole: true, unsealedBeforeRoleChange: true,
                    retainedPartialInitializer: true, bornRecoveryCut: cut);
    }

    private async Task ProbePublicRootLabelCancellationAsync(bool disabled, bool newRole, bool startWritten = false,
        bool unsealedBeforeRoleChange = false, bool retainedPartialInitializer = false, MirrorPulseNamespacePermissionPhase? pendingOriginalPhase = null,
        MirrorPulseNamespaceBornMemberRecoveryCheckpoint? bornRecoveryCut = null)
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
        MirrorPulseRootRenameHistory? preparedBeforeRoleChange = null;
        Guid fileMemberId; Guid? retainedInitializer = null; MirrorPulseNamespacePermissionIntent? pendingOriginalIntent = null;
        CancellationBornCut? retainedBornCut = null;
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
                if (newRole)
                {
                    await role.RunNamespaceOperationAsync(async () => preparedBeforeRoleChange = await new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router)
                        .PrepareAsync(root.RootId, "Renamed", deadline.Token));
                    if (!unsealedBeforeRoleChange)
                    {
                        var cancellationMoves = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                        _ = await cancellationMoves.PrepareRootLabelAsync(preparedBeforeRoleChange!.Intent.OperationId, deadline.Token);
                    }
                    else Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(preparedBeforeRoleChange!.Intent.OperationId, deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(preparedBeforeRoleChange.Intent.OperationId, deadline.Token));
                    if (startWritten)
                        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, preparedBeforeRoleChange.Intent.OperationId, DateTimeOffset.UtcNow), deadline.Token);
                }
            }

            await using var currentRole = new MirrorPulseNamespaceExecutionSession();
            Assert.AreNotEqual(priorRole, currentRole.RoleSid.Value);
            await using var currentCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            await using var currentActor = new MirrorPulseNamespaceActor();
            await using var currentScheduler = new MirrorPulseInstanceScheduler();
            if (retainedPartialInitializer)
            {
                retainedInitializer = Guid.NewGuid();
                await using var partialPermissions = new MirrorPulseNamespacePermissionCoordinator(currentCatalog);
                var partialState = new MirrorPulseCfSharpStateSession(paths);
                await using var partialFileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(partialState).WithContentProvider(source).Build();
                await currentRole.RunNamespaceOperationAsync(async () => await partialFileSystem.StartAsync(deadline.Token));
                if (pendingOriginalPhase is null)
                {
                    var partialRecovery = new MirrorPulseNamespaceCurrentTreeRecoveryCoordinator(partialFileSystem, currentCatalog, router,
                        currentRole, currentActor, currentScheduler, partialPermissions)
                    {
                        Progress = phase =>
                        {
                            if (phase == "NamespaceOriginalMemberCompleted")
                                throw new IOException("Deliberate fixture interruption after the original root role settles.");
                        }
                    };
                    var interruption = await Assert.ThrowsExactlyAsync<IOException>(() => partialRecovery.RecoverRootAsync(
                        retainedInitializer.Value, root.RootId, deadline.Token).AsTask());
                    Assert.AreEqual("Deliberate fixture interruption after the original root role settles.", interruption.Message);
                }
                else
                    pendingOriginalIntent = await PreparePendingOriginalCancellationFixtureAsync(partialFileSystem, currentCatalog, router,
                        currentActor, partialPermissions, root, retainedInitializer.Value, pendingOriginalPhase.Value, deadline.Token);
                Assert.IsNull((await currentCatalog.ReadNamespaceInitializationStatusAsync(retainedInitializer.Value, deadline.Token)).Completion);
                Assert.IsTrue((await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).Contains(root.RootId));
                if (bornRecoveryCut is { } cut)
                    retainedBornCut = await InterruptOriginalCancellationBornFixtureAsync(partialFileSystem, currentCatalog, router, currentRole,
                        currentActor, currentScheduler, partialPermissions, root, preparedBeforeRoleChange!.Intent.OperationId, cut, deadline.Token);
            }
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var demand = new MirrorPulseDemandProvider(router, ranges, namespaceWork: work, catalog: currentCatalog);
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: demand, router: router, catalog: currentCatalog,
                namespaceExecution: currentRole, namespaceActor: currentActor, scheduler: currentScheduler, namespaceWork: work, initializeNamespaceOnStart: true);
            await using var runtime = factory.Create(paths);
            await runtime.StartAsync(deadline.Token);
            Assert.AreEqual(!newRole, runtime.NamespaceStartupResults.Single(item => item.RootId == root.RootId).Admitted);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.CancelManagedRootRenameAsync(Guid.Empty, deadline.Token).AsTask());
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => runtime.CancelManagedRootRenameAsync(Guid.NewGuid(), deadline.Token).AsTask());
            var original = preparedBeforeRoleChange ?? await runtime.PrepareManagedRootRenameAsync(root.RootId, "Renamed", deadline.Token);
            if (startWritten)
            {
                var start = await currentCatalog.ReadNamespaceMemberMoveStartAsync(original.Intent.OperationId, deadline.Token);
                Assert.IsNotNull(start);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token).AsTask());
                Assert.AreEqual(start, await currentCatalog.ReadNamespaceMemberMoveStartAsync(original.Intent.OperationId, deadline.Token));
                Assert.IsNull(await currentCatalog.ReadRootLabelCancellationAsync(original.Intent.OperationId, deadline.Token));
                Assert.AreEqual(original.Intent.OperationId, (await currentCatalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token))!.OperationId);
                Assert.AreEqual("Docs", router.FindRegistration(root.RootId)!.DirectoryName);
                Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Renamed"))); Assert.IsFalse(work.IsAdmitted(root.RootId));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, "Docs/left/nested/pending.bin"), deadline.Token));
                Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, ranges.Calls);
                TestContext.WriteLine($"PublicRootLabelCancellationRefusalActual: disabled={disabled}; newRole=True; startWrittenButUnmoved=True; originalStartPreserved=True; originalFencePreserved=True; cancellationAbsent=True; sourceReads=0.");
                return;
            }
            var initializerBeforeCancellation = retainedInitializer is { } retainedOperation
                ? await currentCatalog.ReadNamespaceInitializationStatusAsync(retainedOperation, deadline.Token) : null;
            var expectedInitializerOwner = retainedPartialInitializer
                ? await currentCatalog.ReadRootLabelCancellationRecoveryOwnerAsync(original.Intent.OperationId, deadline.Token) : null;
            if (retainedPartialInitializer && pendingOriginalPhase is null && bornRecoveryCut is null)
            {
                var partialTree = await currentCatalog.ReadRootLabelCancellationTreeAsync(original.Intent.OperationId, deadline.Token);
                Assert.AreEqual(expectedInitializerOwner, partialTree.RecoveryOwner);
                Assert.HasCount(4, partialTree.Members);
                Assert.AreEqual(currentRole.RoleSid.Value,
                    partialTree.Members.Single(item => item.Context.Member.MemberId == partialTree.RootMemberId).Context.Protection.RoleSid);
                Assert.IsTrue(partialTree.Members.Any(item => item.Context.Member.MemberId != partialTree.RootMemberId &&
                    item.Context.Protection.RoleSid != currentRole.RoleSid.Value));
                // Closed metadata observations precede the original native cancellation guard.
                // Predicted inheritance is diagnostic only; it never replaces verified protection.
                var inventory = partialTree.Members.Append(partialTree.Anchor).ToDictionary(item => item.Context.Member.MemberId);
                foreach (var observedMember in partialTree.Members)
                {
                    var context = observedMember.Context;
                    var selected = await runtime.InspectNamespaceMemberAsync(context.Location.RelativePath, context.Member.IsDirectory, deadline.Token);
                    string localPath = Path.Combine(paths.SyncRootPath, context.Location.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    FileSystemSecurity actual = context.Member.IsDirectory
                        ? new DirectoryInfo(localPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                        : new FileInfo(localPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                    string dacl = actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                    var parent = inventory[context.Location.ParentMemberId!.Value];
                    string parentPath = Path.Combine(paths.SyncRootPath, parent.Context.Location.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    string parentDacl = new DirectoryInfo(parentPath).GetAccessControl(AccessControlSections.Access)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                    string inherited = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parentDacl, context.Member.IsDirectory);
                    TestContext.WriteLine($"LegacyCancellationObjectObservation: disabled={disabled}; origin={context.Member.Origin}; directory={context.Member.IsDirectory}; " +
                        $"nativeMemberMatches={selected.MemberId == context.Member.MemberId}; parentMatches={selected.ActualParentMemberId == context.Location.ParentMemberId}; " +
                        $"pathMatches={selected.RelativePath == context.Location.RelativePath}; ownerMatches={actual.GetOwner(typeof(SecurityIdentifier))?.Value == context.Member.OwnerSid}; " +
                        $"verifiedDaclMatches={dacl == context.Verification.Dacl}; expectedDaclMatches={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(observedMember.ExpectedDacl, dacl)}; " +
                        $"actualParentInheritanceMatches={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(inherited, dacl)}; " +
                        $"nativeDaclProtected={new RawSecurityDescriptor(dacl).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)}; " +
                        $"retainedCurrentRole={context.Protection.RoleSid == currentRole.RoleSid.Value}; diagnosticOnly=True.");
                }
            }
            var cancelled = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, cancelled.Intent.Phase);
            Assert.IsTrue(cancelled.Intent.CancellationOperationId == original.Intent.OperationId ||
                cancelled.Intent.UnsealedCancellationOperationId == original.Intent.OperationId);
            Assert.AreEqual("Docs", cancelled.CurrentDirectoryName); Assert.AreEqual(4, cancelled.MemberCount);
            Assert.AreEqual(!retainedPartialInitializer, cancelled.CurrentTreeAudited);
            Assert.AreEqual(!disabled && !retainedPartialInitializer, cancelled.NamespaceReady);
            var unsealedRecord = await currentCatalog.ReadUnsealedRootLabelCancellationAsync(original.Intent.OperationId, deadline.Token);
            var record = await currentCatalog.ReadRootLabelCancellationAsync(original.Intent.OperationId, deadline.Token) ?? unsealedRecord?.Observation;
            Assert.IsNotNull(record); Assert.AreEqual(cancelled.CancelledAt, record.VerifiedAt);
            if (!newRole || unsealedBeforeRoleChange)
            {
                Assert.IsNotNull(unsealedRecord); Assert.HasCount(4, unsealedRecord.Inventory.Entries);
                Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveAsync(original.Intent.OperationId, deadline.Token));
                Assert.IsNull(await currentCatalog.ReadNamespaceDirectoryMoveSnapshotAsync(original.Intent.OperationId, deadline.Token));
            }
            Assert.IsFalse(record.NativeMoveObserved); Assert.IsFalse(record.DurableProjectionCommitted); Assert.IsFalse(record.RequiresFullRescan);
            Assert.AreEqual(0, record.DurableStateEntriesUpdated); Assert.HasCount(4, record.Members);
            Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveStartAsync(original.Intent.OperationId, deadline.Token));
            Assert.IsNull(await currentCatalog.ReadNamespaceDirectoryMoveCompletionAsync(original.Intent.OperationId, deadline.Token));
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Renamed")));
            var retained = await currentCatalog.ReadManagedRootRenameHistoryAsync(original.Intent.OperationId, deadline.Token);
            Assert.IsNotNull(retained); Assert.AreEqual(original.Proof, retained.Proof); Assert.IsNull(retained.Observation);
            var firstReplay = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(cancelled.Intent, firstReplay.Intent); Assert.AreEqual(cancelled.CancelledAt, firstReplay.CancelledAt);
            Assert.AreEqual(!disabled && !retainedPartialInitializer, firstReplay.NamespaceReady);
            if (retainedInitializer is { } originalInitializer)
            {
                if (retainedBornCut is not null)
                {
                    var actionHistory = await currentCatalog.ReadNamespaceBornMemberRecoveryActionsAsync(retainedBornCut.Action.Child.Member.MemberId, deadline.Token);
                    var originalAction = actionHistory.Single(action => action.ActionId == retainedBornCut.Action.ActionId);
                    Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(retainedBornCut.Action), System.Text.Json.JsonSerializer.Serialize(originalAction));
                    var completedAction = await currentCatalog.ReadNamespaceBornMemberRecoveryCompletionAsync(originalAction.ActionId, deadline.Token);
                    Assert.IsNotNull(completedAction); Assert.AreEqual(originalAction.ActionId, completedAction.ProtectionId);
                    if (retainedBornCut.Completion is not null) Assert.AreEqual(retainedBornCut.Completion, completedAction);
                    TestContext.WriteLine($"PublicCancellationBornCutRecoveredActual: disabled={disabled}; cut={bornRecoveryCut}; originalActionPreserved=True; sameActionCompleted=True; originalCompletionNotRefreshed=True; hostRuntimeResumed=True; independentProcessRestart=False.");
                }
                if (pendingOriginalIntent is not null)
                {
                    var settledPermission = await currentCatalog.ReadNamespacePermissionChangeAsync(pendingOriginalIntent.OperationId, deadline.Token);
                    Assert.IsNotNull(settledPermission); Assert.AreEqual(pendingOriginalIntent, settledPermission.Intent);
                    Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, settledPermission.Phase);
                    Assert.AreNotEqual(currentRole.RoleSid.Value, settledPermission.Intent.RoleSid);
                    Assert.IsNotNull(unsealedRecord);
                    try
                    {
                        var witness = unsealedRecord.Observation.Members.SingleOrDefault(item => item.Native.LocalObject == pendingOriginalIntent.LocalObject);
                        var witnessedPermission = witness is null ? null
                            : await currentCatalog.ReadNamespacePermissionChangeAsync(witness.ProtectionId, deadline.Token);
                        var verified = witnessedPermission?.Verification;
                        TestContext.WriteLine($"PendingOriginalCancellationWitnessObservation: disabled={disabled}; phase={pendingOriginalPhase}; " +
                            $"sameBinding={witness is not null}; originalOperationSelected={witness?.ProtectionId == pendingOriginalIntent.OperationId}; " +
                            $"witnessedPermissionExists={witnessedPermission is not null}; sameOriginalEvidence={witnessedPermission?.Intent.EvidenceId == pendingOriginalIntent.EvidenceId}; " +
                            $"witnessedRoleMatchesCurrent={witnessedPermission?.Intent.RoleSid == currentRole.RoleSid.Value}; " +
                            $"witnessedPermissionVerified={witnessedPermission?.Phase == MirrorPulseNamespacePermissionPhase.Verified}; " +
                            $"witnessedNativeMatchesVerification={verified is not null && witness is not null && verified.Dacl == witness.Native.Dacl}; " +
                            $"witnessedNativeMatchesOriginalTarget={witness is not null && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(pendingOriginalIntent.TargetDacl, witness.Native.Dacl)}; " +
                            $"witnessedExpectedMatchesOriginalTarget={witnessedPermission is not null && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(pendingOriginalIntent.TargetDacl, witnessedPermission.Intent.ExpectedDacl)}; " +
                            $"witnessedVerificationAfterOriginal={verified is not null && verified.ObservedAt > settledPermission.Verification!.ObservedAt}; diagnosticOnly=True.");
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        TestContext.WriteLine($"PendingOriginalCancellationWitnessObservation: disabled={disabled}; phase={pendingOriginalPhase}; " +
                            $"observationUnavailable=True; failureKind={error.GetType().Name}; diagnosticOnly=True.");
                    }
                    var rootObservation = unsealedRecord.Observation.Members.Single(item => item.ProtectionId == pendingOriginalIntent.OperationId);
                    Assert.AreEqual(settledPermission.Verification!.Dacl, rootObservation.Native.Dacl);
                    Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(pendingOriginalIntent.TargetDacl, rootObservation.Native.Dacl));
                }
                Assert.IsNotNull(unsealedRecord); Assert.AreEqual(2, unsealedRecord.Version);
                Assert.AreEqual(originalInitializer, unsealedRecord.RecoveryOwner!.InitializationOperationId);
                Assert.AreEqual(expectedInitializerOwner, unsealedRecord.RecoveryOwner);
                var pendingInitializer = await currentCatalog.ReadNamespaceInitializationStatusAsync(originalInitializer, deadline.Token);
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(initializerBeforeCancellation),
                    System.Text.Json.JsonSerializer.Serialize(pendingInitializer));
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                var completedInitializer = await runtime.RecoverNamespaceRootAsync(originalInitializer, root.RootId, deadline.Token);
                Assert.AreEqual(originalInitializer, completedInitializer.OperationId);
                Assert.AreEqual(currentRole.RoleSid.Value, completedInitializer.RoleSid);
                Assert.AreEqual(root.RootId, completedInitializer.ScopeRoot);
                Assert.IsEmpty(await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(unsealedRecord),
                    System.Text.Json.JsonSerializer.Serialize(await currentCatalog.ReadUnsealedRootLabelCancellationAsync(original.Intent.OperationId, deadline.Token)));
                if (disabled) Assert.ThrowsExactly<IOException>(() => router.ResolveUploadPath(root.InstanceId, root.UniquenessKey, "source.bin"));
                Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, ranges.Calls);
            }

            var later = await runtime.PrepareManagedRootRenameAsync(root.RootId, "FinalLabel", deadline.Token);
            _ = await runtime.ExecuteManagedRootRenameAsync(later.Intent.OperationId, deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.CancelManagedRootRenameAsync(later.Intent.OperationId, deadline.Token).AsTask());
            var replay = await runtime.CancelManagedRootRenameAsync(original.Intent.OperationId, deadline.Token);
            Assert.AreEqual(cancelled.Intent, replay.Intent); Assert.AreEqual(cancelled.CancelledAt, replay.CancelledAt);
            Assert.AreEqual("FinalLabel", replay.CurrentDirectoryName); Assert.IsTrue(replay.CurrentTreeAudited);
            Assert.AreEqual(!disabled, replay.NamespaceReady);
            Assert.AreEqual(retained, await currentCatalog.ReadManagedRootRenameHistoryAsync(original.Intent.OperationId, deadline.Token));
            Assert.AreEqual("FinalLabel", router.FindRegistration(root.RootId)!.DirectoryName);
            Assert.AreEqual(fileMemberId, (await runtime.InspectNamespaceMemberAsync("FinalLabel/left/nested/pending.bin", false, deadline.Token)).MemberId);
            string payload = Path.Combine(paths.SyncRootPath, "FinalLabel", "left", "nested", "pending.bin");
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
            Assert.IsNull(await currentCatalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
            Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, ranges.Calls);
            if (retainedPartialInitializer)
                TestContext.WriteLine($"PublicLegacyInitializerCancellationActual: disabled={disabled}; originalInitializer=True; originalIntentFingerprint=True; mixedVerifiedRoles=True; actualPublicNotMoved=True; wholeMembers=4; originalCancellationKeepsInitializerPending=True; explicitOriginalInitializerResumed=True; freshCurrentRoleAudit=True; laterLabelAndHistoricalCancellationPreserved=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
            else if (unsealedBeforeRoleChange)
                TestContext.WriteLine($"PublicUnsealedRootCancellationActual: disabled={disabled}; newRole=True; originalOperation=True; actualPublicNotMoved=True; completeMembers=4; noMoveAdmission=True; noNativeStart=True; separateCurrentRecovery=True; freshCurrentAudit=True; activeReady={!disabled}; immutableHistoricalReplay=True; latestLabelPreserved=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
            else
                TestContext.WriteLine($"PublicRootLabelCancellationActual: disabled={disabled}; newRole={newRole}; originalOperation=True; actualPublicNotMoved=True; completeMembers=4; noNativeStart=True; separateCurrentRecovery=True; freshCurrentAudit=True; activeReady={!disabled}; immutableHistoricalReplay=True; latestLabelPreserved=True; completedCancellationRefused=True; sourceReads=0; independentHostRestart=False; signedWorker=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!, additionalRootName: "FinalLabel");
            DeleteFixture(directory);
        }
    }
}
