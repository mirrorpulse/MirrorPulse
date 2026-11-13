using System.Security.Principal;
using System.Text.Json;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativePublicRuntimeExecutesUnsealedBornRootLabelAcrossDisposedRoles(bool disabled)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.unsealed-born-root-label"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false),
            disabled ? RootRegistrationState.Disabled : RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        byte[] bytes = "synthetic-unsealed-born-bytes"u8.ToArray();
        const string sourceName = "Docs";
        const string targetName = "Renamed";
        const string childName = "original.bin";
        MirrorPulseNamespaceMemberRegistration rootMember = null!;
        MirrorPulseNamespaceMemberRegistration child = null!;
        MirrorPulseNamespaceBirthObservation rootObservation = null!;
        MirrorPulseNamespaceBirthObservation childObservation = null!;
        MirrorPulseRootRenameHistory prepared = null!;
        string priorRole = string.Empty;
        MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker firstProbe = null!;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 7, 2]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            // The first runtime auto-creates the born root. Preparation keeps only public proof;
            // subtree admission waits for a fully disposed role to hand the born objects over.
            await using (var role = new MirrorPulseNamespaceExecutionSession())
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token))
            await using (var actor = new MirrorPulseNamespaceActor())
            await using (var scheduler = new MirrorPulseInstanceScheduler())
            await using (var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]))
            {
                priorRole = role.RoleSid.Value;
                await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                var boundary = OpenUnsealedBornRootBoundary(role, router, work, catalog);
                firstProbe = boundary.Probe;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: boundary.Provider, router: router, catalog: catalog,
                    namespaceExecution: role, namespaceActor: actor, scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true);
                await using var runtime = factory.Create(paths);
                await runtime.StartAsync(deadline.Token);
                var started = runtime.NamespaceStartupResults.Single();
                Assert.AreEqual(root.RootId, started.RootId);
                Assert.AreEqual(!disabled, started.Admitted);
                Assert.IsNull(started.RecoveryCode);
                Assert.AreEqual(!disabled, work.IsAdmitted(root.RootId));
                var inspectedRoot = await runtime.InspectNamespaceMemberAsync(sourceName, true, deadline.Token);
                rootMember = (await catalog.ReadNamespaceMemberAsync(inspectedRoot.MemberId, deadline.Token))!;
                Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, rootMember.Origin);
                Assert.AreEqual(root.RootId, rootMember.RootId);
                Assert.AreEqual(sourceName, rootMember.RelativePath);
                child = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), rootMember.MemberId, childName, false, deadline.Token);
                Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, child.Origin);
                Assert.AreEqual(rootMember.MemberId, child.ParentMemberId);
                Assert.AreEqual(sourceName + "/" + childName, child.RelativePath);
                string childPath = Path.Combine(paths.SyncRootPath, sourceName, childName);
                await using (var writer = new FileStream(childPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    await writer.WriteAsync(bytes, deadline.Token);
                    writer.SetLength(bytes.Length);
                    writer.Flush(true);
                }
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(childPath, deadline.Token));
                Assert.AreEqual(child.MemberId, (await runtime.InspectNamespaceMemberAsync(sourceName + "/" + childName, false, deadline.Token)).MemberId);
                rootObservation = (await catalog.ReadNamespaceBirthObservationAsync(rootMember.OriginId, deadline.Token))!;
                childObservation = (await catalog.ReadNamespaceBirthObservationAsync(child.OriginId, deadline.Token))!;
                Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                prepared = await runtime.PrepareManagedRootRenameAsync(root.RootId, targetName, deadline.Token);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, prepared.Intent.Phase);
                Assert.AreEqual(root.RootId, prepared.Intent.RootId);
                Assert.AreEqual(sourceName, prepared.Intent.SourceName);
                Assert.AreEqual(targetName, prepared.Intent.TargetName);
                Assert.IsNotNull(prepared.Proof);
                Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(prepared.Intent.OperationId, deadline.Token));
                Assert.AreEqual(priorRole, (await catalog.ReadLatestNamespaceBirthProtectionAsync(rootMember.OriginId, deadline.Token))!.RoleSid);
                Assert.AreEqual(priorRole, (await catalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, deadline.Token))!.RoleSid);
                Assert.IsFalse(work.IsAdmitted(root.RootId));
                Assert.IsEmpty(firstProbe.Calls);
            }

            await using var currentRole = new MirrorPulseNamespaceExecutionSession();
            Assert.AreNotEqual(priorRole, currentRole.RoleSid.Value);
            await using var currentCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
            root = (await currentCatalog.ReadAdapterTopologyAsync(deadline.Token)).Roots.Single();
            Assert.AreEqual(disabled ? RootRegistrationState.Disabled : RootRegistrationState.Active, root.State);
            Assert.AreEqual(sourceName, root.DirectoryName);
            router.ReplaceRegistrations([root], await currentCatalog.ReadManagedRootNamesAsync(deadline.Token));
            await using var currentActor = new MirrorPulseNamespaceActor();
            await using var currentScheduler = new MirrorPulseInstanceScheduler();
            await using var currentWork = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var currentBoundary = OpenUnsealedBornRootBoundary(currentRole, router, currentWork, currentCatalog);
            var currentFactory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: currentBoundary.Provider, router: router, catalog: currentCatalog,
                namespaceExecution: currentRole, namespaceActor: currentActor, scheduler: currentScheduler, namespaceWork: currentWork, initializeNamespaceOnStart: true);
            await using var currentRuntime = currentFactory.Create(paths);
            await currentRuntime.StartAsync(deadline.Token);
            var reopened = currentRuntime.NamespaceStartupResults.Single();
            Assert.AreEqual(root.RootId, reopened.RootId);
            Assert.IsFalse(reopened.Admitted);
            Assert.AreEqual("PendingNamespaceOperationRecoveryRequired", reopened.RecoveryCode);
            Assert.IsFalse(currentWork.IsAdmitted(root.RootId));
            var pending = await currentCatalog.ReadNamespacePendingOperationsAsync(root.RootId, deadline.Token);
            Assert.AreEqual(prepared.Intent.OperationId, pending.Single(item => item.Kind == MirrorPulseNamespacePendingOperationKind.RootLabel).OperationId);
            Assert.IsNull(await currentCatalog.ReadNamespaceMemberMoveAsync(prepared.Intent.OperationId, deadline.Token));
            Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, (await currentCatalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId, deadline.Token))!.Intent.Phase);
            Assert.AreEqual(rootMember, await currentCatalog.ReadNamespaceMemberAsync(rootMember.MemberId, deadline.Token));
            Assert.AreEqual(child, await currentCatalog.ReadNamespaceMemberAsync(child.MemberId, deadline.Token));
            Assert.AreEqual(rootMember.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(sourceName, true, deadline.Token)).MemberId);
            Assert.AreEqual(child.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(sourceName + "/" + childName, false, deadline.Token)).MemberId);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.SyncRootPath, sourceName, childName), deadline.Token));
            Assert.AreEqual(priorRole, (await currentCatalog.ReadLatestNamespaceBirthProtectionAsync(rootMember.OriginId, deadline.Token))!.RoleSid);
            Assert.AreEqual(priorRole, (await currentCatalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, deadline.Token))!.RoleSid);
            var initializersBeforeExecute = await currentCatalog.ReadNamespaceInitializationsAsync(cancellationToken: deadline.Token);
            Assert.IsNotEmpty(initializersBeforeExecute);
            Assert.IsTrue(initializersBeforeExecute.All(item => item.Completion is not null));
            Assert.IsEmpty(await currentCatalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
            CollectionAssert.AreEqual(new[] { root.RootId },
                (await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)).ToArray());
            Assert.IsEmpty(firstProbe.Calls);
            Assert.IsEmpty(currentBoundary.Probe.Calls);

            var executed = await currentRuntime.ExecuteManagedRootRenameAsync(prepared.Intent.OperationId, deadline.Token);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, executed.Intent.Phase);
            Assert.AreEqual(prepared.Intent.OperationId, executed.Intent.OperationId);
            Assert.AreEqual(targetName, executed.CurrentDirectoryName);
            Assert.AreEqual(2, executed.MemberCount);
            Assert.IsTrue(executed.CurrentTreeAudited);
            Assert.AreEqual(!disabled, executed.NamespaceReady);
            Assert.AreEqual(!disabled, currentWork.IsAdmitted(root.RootId));
            var history = (await currentCatalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId, deadline.Token))!;
            Assert.AreEqual(prepared.Proof, history.Proof);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, history.Intent.Phase);
            var move = (await currentCatalog.ReadNamespaceMemberMoveAsync(prepared.Intent.OperationId, deadline.Token))!;
            Assert.AreEqual(currentRole.RoleSid.Value, move.RoleSid);
            var completion = (await currentCatalog.ReadNamespaceDirectoryMoveCompletionAsync(prepared.Intent.OperationId, deadline.Token))!;
            Assert.HasCount(2, completion.Entries);
            Assert.IsTrue(completion.Entries.Select(entry => entry.MemberId).ToHashSet()
                .SetEquals(new[] { rootMember.MemberId, child.MemberId }));
            Assert.IsTrue(completion.PublicResult.NativeMoveObserved && completion.PublicResult.DurableProjectionCommitted);
            Assert.IsFalse(completion.PublicResult.RequiresFullRescan);
            Assert.AreEqual(rootMember, await currentCatalog.ReadNamespaceMemberAsync(rootMember.MemberId, deadline.Token));
            Assert.AreEqual(child, await currentCatalog.ReadNamespaceMemberAsync(child.MemberId, deadline.Token));
            Assert.AreEqual(rootObservation, await currentCatalog.ReadNamespaceBirthObservationAsync(rootMember.OriginId, deadline.Token));
            Assert.AreEqual(childObservation, await currentCatalog.ReadNamespaceBirthObservationAsync(child.OriginId, deadline.Token));
            Assert.AreEqual(targetName, (await currentCatalog.ReadNamespaceMemberLocationAsync(rootMember.MemberId, deadline.Token)).RelativePath);
            var childLocation = await currentCatalog.ReadNamespaceMemberLocationAsync(child.MemberId, deadline.Token);
            Assert.AreEqual(targetName + "/" + childName, childLocation.RelativePath);
            Assert.AreEqual(rootMember.MemberId, childLocation.ParentMemberId);
            Assert.AreEqual(rootMember.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(targetName, true, deadline.Token)).MemberId);
            Assert.AreEqual(child.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(targetName + "/" + childName, false, deadline.Token)).MemberId);
            Assert.AreEqual(currentRole.RoleSid.Value, (await currentCatalog.ReadNamespaceMemberProtectionSnapshotAsync(rootMember.MemberId, deadline.Token))!.Context.Protection.RoleSid);
            Assert.AreEqual(currentRole.RoleSid.Value, (await currentCatalog.ReadNamespaceMemberProtectionSnapshotAsync(child.MemberId, deadline.Token))!.Context.Protection.RoleSid);
            Assert.AreEqual(currentRole.RoleSid.Value, (await currentCatalog.ReadLatestNamespaceBirthProtectionAsync(rootMember.OriginId, deadline.Token))!.RoleSid);
            Assert.AreEqual(currentRole.RoleSid.Value, (await currentCatalog.ReadLatestNamespaceBirthProtectionAsync(child.OriginId, deadline.Token))!.RoleSid);
            Assert.AreNotEqual(priorRole, currentRole.RoleSid.Value);
            var initializersAfter = await currentCatalog.ReadNamespaceInitializationsAsync(cancellationToken: deadline.Token);
            foreach (var prior in initializersBeforeExecute)
            {
                var retained = initializersAfter.Single(item => item.Intent.OperationId == prior.Intent.OperationId);
                Assert.AreEqual(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(retained));
            }
            foreach (var recovery in initializersAfter.Where(item => initializersBeforeExecute.All(prior => prior.Intent.OperationId != item.Intent.OperationId)))
            {
                Assert.IsNotNull(recovery.Completion);
                Assert.AreEqual(currentRole.RoleSid.Value, recovery.Completion.RoleSid);
                Assert.AreEqual(root.RootId, recovery.Completion.ScopeRoot);
            }
            Assert.IsEmpty(await currentCatalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
            Assert.IsEmpty(await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
            Assert.IsNull(await currentCatalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
            Assert.AreEqual(targetName, router.FindRegistration(root.RootId)!.DirectoryName);
            Assert.AreEqual(root.State, router.FindRegistration(root.RootId)!.State);
            Assert.AreEqual(targetName, Directory.EnumerateDirectories(paths.SyncRootPath).Select(Path.GetFileName).Single());
            string payload = Path.Combine(paths.SyncRootPath, targetName, childName);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(payload), deadline.Token));
            if (disabled)
            {
                Assert.IsFalse(currentWork.TryEnter([root.RootId], out var ordinary));
                Assert.IsNull(ordinary);
                Assert.ThrowsExactly<IOException>(() => router.ResolveUploadPath(root.InstanceId, root.UniquenessKey, "source.bin"));
            }
            string settledInitializers = JsonSerializer.Serialize(initializersAfter);
            var replay = await currentRuntime.ExecuteManagedRootRenameAsync(prepared.Intent.OperationId, deadline.Token);
            Assert.AreEqual(executed.Intent, replay.Intent);
            Assert.AreEqual(targetName, replay.CurrentDirectoryName);
            Assert.AreEqual(2, replay.MemberCount);
            Assert.IsTrue(replay.CurrentTreeAudited);
            Assert.AreEqual(!disabled, replay.NamespaceReady);
            Assert.AreEqual(history, await currentCatalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId, deadline.Token));
            Assert.AreEqual(settledInitializers, JsonSerializer.Serialize(await currentCatalog.ReadNamespaceInitializationsAsync(cancellationToken: deadline.Token)));
            Assert.IsEmpty(await currentCatalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
            Assert.IsEmpty(await currentCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
            Assert.AreEqual(rootMember.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(targetName, true, deadline.Token)).MemberId);
            Assert.AreEqual(child.MemberId, (await currentRuntime.InspectNamespaceMemberAsync(targetName + "/" + childName, false, deadline.Token)).MemberId);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload, deadline.Token));
            Assert.IsEmpty(firstProbe.Calls);
            Assert.IsEmpty(currentBoundary.Probe.Calls);
            TestContext.WriteLine($"PublicUnsealedBornRootExecutionActual: disabled={disabled}; autoCreatedProtectedBirth=True; bornChild=True; exactBytes=True; " +
                "unsealedPreparation=True; independentRoleDisposed=True; newRole=True; pendingInitializerCompleted=True; stableRootAndMemberIds=True; " +
                "completeMembers=2; historicalReplay=True; sourceCalls=0; independentHostRestart=False; signedWorker=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreRootLabelExecutionFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static (MirrorPulseDemandProvider Provider, MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker Probe) OpenUnsealedBornRootBoundary(
        MirrorPulseNamespaceExecutionSession execution, MirrorPulseRootRouter router, MirrorPulseNamespaceWorkAdmission work, MirrorPulseProductCatalog catalog)
    {
        var probe = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
        var source = new MirrorPulseNormalUserWorkerTransport(execution, probe, probe, probe, probe, probe);
        return (new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work, catalog), probe);
    }
}
