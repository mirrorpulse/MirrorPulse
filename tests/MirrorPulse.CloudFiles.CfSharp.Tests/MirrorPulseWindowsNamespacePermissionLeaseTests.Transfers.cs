using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [TestCategory("NativeCloudFiles")]
    public Task NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification() => RunNativeHostTransferAsync(deleteSource: false);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes() => RunNativeHostTransferAsync(deleteSource: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public Task NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences() =>
        RunNativeHostTransferAsync(deleteSource: true, retireLocal: true);

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners()
    {
        foreach (string fault in new[] { "before-native", "after-native", "after-receipt", "native-receipt-write", "name-settlement-write" })
            await RunNativeHostTransferAsync(deleteSource: true, retireLocal: true, retirementFault: fault);
    }

    private async Task RunNativeHostTransferAsync(bool deleteSource, bool retireLocal = false, string? retirementFault = null)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool sameInstance = attempt == 1;
            string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            var registry = new CfSharpMirrorPulseCloudRootRegistry();
            var adapter = AdapterId.Parse("example.transfer-native"); var firstInstance = InstanceId.New();
            var sourceRoot = AdapterRootRegistrationMapper.Map(adapter, firstInstance, new("docs", "Docs", "Docs", false),
                RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            var targetRoot = AdapterRootRegistrationMapper.Map(adapter, sameInstance ? firstInstance : InstanceId.New(), new("archive", "Archive", "Archive", false),
                RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, [sourceRoot, targetRoot]);
            using var caller = WindowsIdentity.GetCurrent();
            await using var role = new MirrorPulseNamespaceExecutionSession();
            await using var nextRole = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([sourceRoot.RootId, targetRoot.RootId]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var provider = new ProtectedPermissionNoSourceProvider();
            byte[] bytes = sameInstance ? deleteSource ? new byte[4097] : [] : new byte[2_097_409]; new Random(517).NextBytes(bytes);
            try
            {
                registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [7, 4, 7]));
                await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(provider).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath); Directory.CreateDirectory(fileSystem.GetDirectory("Archive").FullPath);
                string sourcePath = fileSystem.GetFile("Docs/original.bin").FullPath;
                await File.WriteAllBytesAsync(sourcePath, bytes, deadline.Token);
                var originals = new List<MirrorPulseNamespacePermissionPreparation>();
                foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"),
                    fileSystem.GetDirectory("Archive"), fileSystem.GetFile("Docs/original.bin") })
                {
                    SetProtectedFixtureUserOwner(item, caller.User!);
                    await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, deadline.Token);
                    var observed = await lease.InspectAsync(deadline.Token);
                    var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), observed.RootId, observed.LocalObject,
                        observed.RelativePath, observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                    originals.Add(new(baseline, new(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject, baseline.RootId,
                        baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value, baseline.OriginalDacl,
                        MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, baseline.IsDirectory), DateTimeOffset.UtcNow)));
                }
                await catalog.PrepareNamespacePermissionChangesAsync(originals, deadline.Token);
                await using (var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog))
                    await role.RunNamespaceOperationAsync(async () =>
                    {
                        foreach (var original in originals.AsEnumerable().Reverse())
                        {
                            var identity = original.Baseline.IsDirectory ? null : router.CreateFileIdentity(sourceRoot.InstanceId, "docs", "local:original");
                            var applied = await permissions.ApplyProtectedAsync(original.Intent.OperationId,
                                NativeOriginalMemberItem(fileSystem, original.Baseline), router, identity, deadline.Token);
                            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
                        }
                    });
                var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 2),
                    new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                    new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                    [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
                var installed = new InstalledAdapter(manifest, InstallId.New(), Path.GetTempPath(), new(new string('A', 64)),
                    AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                AdapterInstance Instance(InstanceId id) => new(adapter, installed.InstallId, id, "Storage", new Dictionary<string, string>(), [],
                    Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
                await catalog.SaveAdapterTopologyAsync(new([installed], sameInstance ? [Instance(firstInstance)] :
                    [Instance(firstInstance), Instance(targetRoot.InstanceId)], [sourceRoot, targetRoot]), deadline.Token);
                var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
                await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                var member = members.Values.Single(item => !item.IsDirectory);
                var target = members.Values.Single(item => item.RelativePath == "Archive");
                if (deleteSource)
                    await PrepareTransferAcceptedSourceAsync(fileSystem.GetFile("Docs/original.bin"), catalog, router, role, actor,
                        sourceRoot, bytes, deadline.Token);
                var nativeBefore = await fileSystem.GetFile("Docs/original.bin").InspectAsync(deadline.Token);
                long? originalOfficialFileId = null;
                if (retireLocal)
                {
                    await using var read = await state.OpenStore.BeginTransactionAsync(deadline.Token);
                    var originalItem = await read.Items.GetByItemIdAsync(nativeBefore.ItemId!.Value, deadline.Token);
                    Assert.IsNotNull(originalItem); originalOfficialFileId = originalItem.LocalFileId;
                    await read.RollbackAsync(deadline.Token);
                }
                await fileSystem.DisposeAsync();
                var worker = new TransferNativeWorker(role, caller.User!, targetRoot, bytes);
                var transport = new MirrorPulseNormalUserWorkerTransport(role, worker, worker, worker, worker, worker);
                int targetProtocol = 1;
                MirrorPulseTransferWorkerServices Services(MirrorPulseNormalUserWorkerTransport source) =>
                    new(source, source, source, source, id => id == targetRoot.InstanceId ? targetProtocol : 2, source);
                MirrorPulseTransferPublicationCheckpoint? publicationCut = null; int publicationFaults = 0;
                var projectionFault = new MirrorPulseTransferProjectionCommitFault(role.RoleSid);
                int retirementFaults = 0, nativeDeleteCalls = 0; bool sqlFaultArmed = false;
                async ValueTask RetirementCheckpoint(MirrorPulseTransferSourceRetirementCheckpoint checkpoint, CancellationToken stop)
                {
                    if (checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion)
                    {
                        Assert.AreEqual(1, worker.Deletes); Assert.IsTrue(worker.SourceDeleted);
                        Assert.ThrowsExactly<IOException>(() =>
                        { using var late = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                        if (!sqlFaultArmed && retirementFault is "native-receipt-write" or "name-settlement-write")
                        {
                            sqlFaultArmed = true;
                            int phase = retirementFault == "native-receipt-write" ? 1 : 2;
                            await ExecuteTransferPublicationFixtureSqlAsync(paths,
                                $"CREATE TRIGGER fail_transfer_retirement_boundary BEFORE UPDATE ON namespace_member_retirements WHEN NEW.phase={phase} BEGIN SELECT RAISE(ABORT,'owned-transfer-retirement-gap'); END;", stop);
                        }
                    }
                    bool selected = retirementFault switch
                    {
                        "before-native" => checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion,
                        "after-native" => checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeDeletion,
                        "after-receipt" => checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.AfterNativeReceipt,
                        _ => false,
                    };
                    if (selected && retirementFaults == 0)
                    {
                        retirementFaults++;
                        throw new InvalidOperationException("Synthetic interruption at the original transfer source retirement boundary.");
                    }
                    if (checkpoint == MirrorPulseTransferSourceRetirementCheckpoint.BeforeNativeDeletion) nativeDeleteCalls++;
                }
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: new MirrorPulseDemandProvider(router, transport, namespaceWork: work),
                    router: router, catalog: catalog, mayDispatch: _ => true,
                    scheduler: scheduler, namespaceExecution: role, namespaceActor: actor, namespaceWork: work, transfers: Services(transport))
                {
                    StateStoreDecorator = projectionFault.Wrap,
                    TransferSourceRetirementCheckpoint = retireLocal ? RetirementCheckpoint : null,
                    TransferPublicationCheckpoint = (checkpoint, _) =>
                    {
                        if (checkpoint != publicationCut) return ValueTask.CompletedTask;
                        publicationCut = null; publicationFaults++;
                        return ValueTask.FromException(new IOException("Synthetic transfer publication cut after durable native facts."));
                    }
                };
                Guid operation = Guid.NewGuid(); MirrorPulseNamespaceTransferCopy verified; MirrorPulseNamespaceRetirement? retired = null;
                await using (var runtime = factory.Create(paths))
                {
                    await runtime.StartAsync(deadline.Token);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                        await runtime.PrepareNamespaceTransferAsync(operation, member.MemberId, target.MemberId, "moved.bin", deadline.Token));
                    await using (var ready = await work.BeginRecoveryAsync([sourceRoot.RootId, targetRoot.RootId], deadline.Token)) ready.CompleteAfterFreshAudit();
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                        await runtime.PrepareNamespaceTransferAsync(operation, member.MemberId, target.MemberId, "moved.bin", deadline.Token));
                    Assert.IsNull(await catalog.ReadNamespaceTransferAsync(operation, deadline.Token));
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token)); targetProtocol = 2;
                    var prepared = await runtime.PrepareNamespaceTransferAsync(operation, member.MemberId, target.MemberId, "moved.bin", deadline.Token);
                    worker.Plan = prepared.Plan;
                    Assert.AreEqual(nativeBefore.LocalBinding!.LocalFileId, prepared.Plan.Source.Context.Member.LocalObject.LocalFileId);
                    Assert.IsNull(prepared.CopyStartedAt); Assert.AreEqual(bytes.LongLength, prepared.Plan.Length);
                    await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(async () =>
                        await runtime.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token));
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(sourcePath), deadline.Token));
                    if (sameInstance)
                    {
                        await File.WriteAllBytesAsync(sourcePath, [9], deadline.Token);
                        await Assert.ThrowsExactlyAsync<IOException>(async () => await runtime.CopyNamespaceTransferAsync(operation, deadline.Token));
                        Assert.AreEqual(0, worker.Uploads); Assert.IsNull((await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!.CopyStartedAt);
                        await File.WriteAllBytesAsync(sourcePath, bytes, deadline.Token);
                    }
                    var accepted = await runtime.CopyNamespaceTransferAsync(operation, deadline.Token);
                    Assert.AreEqual("target-v1", accepted.AcceptedRevision);
                    verified = await runtime.VerifyNamespaceTransferTargetAsync(operation, deadline.Token);
                    Assert.IsNotNull(verified.TargetVerification); Assert.AreEqual("target:original", verified.TargetVerification.RemoteId);
                    verified = await runtime.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token);
                    Assert.IsNotNull(verified.TargetBirth);
                    var targetIdentity = router.CreateFileIdentity(targetRoot.InstanceId, targetRoot.UniquenessKey, "target:original", "target-v1");
                    worker.HydrationIdentity = targetIdentity;
                    Assert.AreEqual(targetIdentity.ItemId, verified.TargetBirth.Plan.ItemId);
                    Assert.AreEqual(targetIdentity.RemoteId, verified.TargetBirth.Plan.RemoteId);
                    Assert.AreNotEqual(prepared.Plan.Source.Identity.ItemId, verified.TargetBirth.Plan.ItemId);
                    Assert.AreEqual(MirrorPulseNamespaceBirthOrigin.TransferTarget, verified.TargetBirth.Birth.Origin);
                    Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(verified.TargetBirth.Birth.OperationId, deadline.Token));
                    Assert.AreEqual(verified, await runtime.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token));
                    IMirrorPulseCloudRuntime publishing = runtime; IMirrorPulseCloudRuntime? secondPublisher = null;
                    MirrorPulseNamespaceWorkAdmission? recoveryWork = null;
                    try
                    {
                        if (!sameInstance && !deleteSource)
                        {
                            projectionFault.Arm(verified.TargetBirth.Plan.ItemId);
                            await Assert.ThrowsExactlyAsync<CloudPlaceholderPersistenceException>(async () =>
                                await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                            Assert.AreEqual(1, projectionFault.Failures);
                            Assert.IsNotNull(await catalog.ReadNamespaceBirthObservationAsync(verified.TargetBirth.Birth.OperationId, deadline.Token));
                            Assert.IsNull(await catalog.ReadNamespaceMemberAsync(verified.TargetBirth.Birth.OperationId, deadline.Token));
                            Assert.IsNull((await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!.TargetPublication);
                        }
                        if (sameInstance && !deleteSource)
                        {
                            publicationCut = MirrorPulseTransferPublicationCheckpoint.AfterObservation;
                            await Assert.ThrowsExactlyAsync<IOException>(async () => await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                            var firstTarget = await catalog.ReadNamespaceBirthObservationAsync(verified.TargetBirth.Birth.OperationId, deadline.Token);
                            Assert.IsNotNull(firstTarget); Assert.IsNull(await catalog.ReadNamespaceMemberAsync(firstTarget.OperationId, deadline.Token));
                            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                            await runtime.DisposeAsync();
                            recoveryWork = new([sourceRoot.RootId, targetRoot.RootId]);
                            var recoveryFactory = new CfSharpMirrorPulseCloudRuntimeFactory(
                                provider: new MirrorPulseDemandProvider(router, transport, namespaceWork: recoveryWork), router: router, catalog: catalog, mayDispatch: _ => true,
                                scheduler: scheduler, namespaceExecution: role, namespaceActor: actor, namespaceWork: recoveryWork, transfers: Services(transport))
                            { TransferPublicationCheckpoint = factory.TransferPublicationCheckpoint, StateStoreDecorator = projectionFault.Wrap };
                            secondPublisher = recoveryFactory.Create(paths); publishing = secondPublisher; await publishing.StartAsync(deadline.Token);
                            publicationCut = MirrorPulseTransferPublicationCheckpoint.AfterRegistration;
                            await Assert.ThrowsExactlyAsync<IOException>(async () => await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                            Assert.IsNotNull(await catalog.ReadNamespaceMemberAsync(firstTarget.OperationId, deadline.Token));
                            Assert.AreEqual(firstTarget, await catalog.ReadNamespaceBirthObservationAsync(firstTarget.OperationId, deadline.Token));
                            await ExecuteTransferPublicationFixtureSqlAsync(paths,
                                "CREATE TRIGGER fail_transfer_publication BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'synthetic-publication-gap'); END;", deadline.Token);
                            try
                            {
                                await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
                                    await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                                Assert.IsNull((await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!.TargetPublication);
                            }
                            finally { await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER fail_transfer_publication;", CancellationToken.None); }
                        }
                        verified = await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token);
                        Assert.IsNotNull(verified.TargetPublication);
                        Assert.IsNotNull(verified.TargetBirth);
                        Assert.AreEqual(verified.TargetBirth.Birth.OperationId, verified.TargetPublication.MemberId);
                        Assert.AreEqual(targetIdentity.ItemId, verified.TargetPublication.ItemId);
                        Assert.AreNotEqual(member.LocalObject, verified.TargetPublication.LocalObject);
                        Assert.AreEqual(verified, await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                        string nativeTarget = Path.Combine(paths.SyncRootPath, "Archive", "moved.bin");
                        var targetBefore = await publishing.InspectNamespaceMemberAsync("Archive/moved.bin", false, deadline.Token);
                        var protection = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(verified.TargetPublication.MemberId, deadline.Token);
                        Assert.IsNotNull(protection);
                        int beforeHydration = Volatile.Read(ref worker.RangeReads);
                        // Read outside the actor and instance gates: the real SDK callback must be able
                        // to reach the normal-user Worker range transport. Empty files need no fetch.
                        byte[] nativeBytes = await File.ReadAllBytesAsync(nativeTarget, deadline.Token);
                        CollectionAssert.AreEqual(bytes, nativeBytes);
                        Assert.AreEqual(verified.Plan.Sha256, Convert.ToHexString(SHA256.HashData(nativeBytes)));
                        if (bytes.Length != 0) Assert.IsGreaterThan(beforeHydration, Volatile.Read(ref worker.RangeReads));
                        else
                        {
                            Assert.AreEqual(beforeHydration, Volatile.Read(ref worker.RangeReads));
                            if (recoveryWork is not null) Assert.IsFalse(recoveryWork.IsAdmitted(targetRoot.RootId), "An empty-file read cannot reopen the recovered root.");
                        }
                        var targetAfter = await publishing.InspectNamespaceMemberAsync("Archive/moved.bin", false, deadline.Token);
                        Assert.AreEqual(targetBefore.MemberId, targetAfter.MemberId); Assert.AreEqual(verified.TargetPublication.MemberId, targetAfter.MemberId);
                        Assert.AreEqual(targetBefore.ActualParentMemberId, targetAfter.ActualParentMemberId);
                        Assert.AreEqual(targetBefore.NativeItemId, targetAfter.NativeItemId); Assert.AreEqual(targetIdentity.ItemId, targetAfter.NativeItemId);
                        Assert.AreEqual(protection, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(targetAfter.MemberId, deadline.Token));
                        string actualDacl = new FileInfo(nativeTarget).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(protection.ExpectedDacl, actualDacl));
                        Assert.AreEqual(verified, await publishing.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                        if (deleteSource)
                        {
                            async Task ExecuteSourcePhase()
                            {
                                if (retireLocal) _ = await publishing.RetireNamespaceTransferSourceAsync(operation, deadline.Token);
                                else _ = await publishing.DeleteNamespaceTransferSourceAsync(operation, deadline.Token);
                            }
                            Assert.AreEqual("source-v1", verified.Plan.SourceRemoteRevision);
                            await using (var writer = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                                await Assert.ThrowsExactlyAsync<IOException>(ExecuteSourcePhase);
                            Assert.IsNull((await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!.SourceDeletion);
                            using (var mappingInput = new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                            using (var mapping = MemoryMappedFile.CreateFromFile(mappingInput, null, Math.Max(1, bytes.Length), MemoryMappedFileAccess.ReadWrite,
                                HandleInheritability.None, leaveOpen: true))
                            using (var view = mapping.CreateViewAccessor())
                            {
                                mappingInput.Dispose();
                                await Assert.ThrowsExactlyAsync<IOException>(ExecuteSourcePhase);
                            }
                            Assert.AreEqual(0, worker.Deletes);
                            Assert.IsNull((await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!.SourceDeletion);
                            worker.DeleteInspection = () => Assert.ThrowsExactly<IOException>(() =>
                            { using var queued = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                            worker.LoseDeleteReply = sameInstance && !retireLocal;
                            if (retireLocal)
                            {
                                MirrorPulseNamespaceRetirement? interrupted = null;
                                if (retirementFault is not null)
                                {
                                    if (retirementFault is "native-receipt-write" or "name-settlement-write")
                                        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await publishing.RetireNamespaceTransferSourceAsync(operation, deadline.Token));
                                    else
                                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await publishing.RetireNamespaceTransferSourceAsync(operation, deadline.Token));
                                    interrupted = await catalog.ReadNamespaceRetirementAsync(verified.Plan.DeleteOperationId, deadline.Token);
                                    Assert.IsNotNull(interrupted); Assert.IsNotNull(interrupted.StartedAt);
                                    Assert.AreEqual(nativeBefore.ItemId, interrupted.StartedItemId);
                                    bool receiptPersisted = retirementFault is "after-receipt" or "name-settlement-write";
                                    Assert.AreEqual(receiptPersisted ? MirrorPulseNamespaceRetirementPhase.NativeDeleted : MirrorPulseNamespaceRetirementPhase.Prepared, interrupted.Phase);
                                    Assert.AreEqual(receiptPersisted, interrupted.TransferReceipt is not null);
                                    Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(verified.Plan.SourceParent.Context.Member.MemberId, "original.bin", deadline.Token));
                                    Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                                    Assert.AreEqual(1, worker.Deletes); Assert.AreEqual(1, worker.Uploads);
                                    if (retirementFault == "before-native") CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                                    else Assert.IsFalse(File.Exists(sourcePath));
                                    if (sqlFaultArmed)
                                    {
                                        retirementFaults++;
                                        await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER fail_transfer_retirement_boundary;", deadline.Token);
                                    }
                                    await publishing.DisposeAsync();
                                    recoveryWork = new([sourceRoot.RootId, targetRoot.RootId]);
                                    var recoveryFactory = new CfSharpMirrorPulseCloudRuntimeFactory(
                                        provider: new MirrorPulseDemandProvider(router, transport, namespaceWork: recoveryWork), router: router, catalog: catalog, mayDispatch: _ => true,
                                        scheduler: scheduler, namespaceExecution: role, namespaceActor: actor, namespaceWork: recoveryWork, transfers: Services(transport))
                                    { TransferSourceRetirementCheckpoint = RetirementCheckpoint };
                                    secondPublisher = recoveryFactory.Create(paths); publishing = secondPublisher; await publishing.StartAsync(deadline.Token);
                                }
                                retired = await publishing.RetireNamespaceTransferSourceAsync(operation, deadline.Token);
                                Assert.AreEqual(1, nativeDeleteCalls); Assert.AreEqual(retirementFault is null ? 0 : 1, retirementFaults);
                                if (interrupted is not null)
                                {
                                    Assert.AreEqual(interrupted.StartedAt, retired.StartedAt);
                                    Assert.AreEqual(interrupted.StartedItemId, retired.StartedItemId);
                                    if (interrupted.TransferReceipt is not null) Assert.AreEqual(interrupted.TransferReceipt, retired.TransferReceipt);
                                }
                                Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, retired.Phase);
                                Assert.AreEqual(retired, await publishing.RetireNamespaceTransferSourceAsync(operation, deadline.Token));
                                verified = (await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!;
                                Assert.IsFalse(File.Exists(sourcePath));
                                Assert.IsNotNull(retired.TransferReceipt); Assert.AreEqual(nativeBefore.ItemId, retired.StartedItemId);
                                Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(verified.Plan.SourceParent.Context.Member.MemberId, "original.bin", deadline.Token));
                            }
                            else if (sameInstance)
                            {
                                await Assert.ThrowsExactlyAsync<IOException>(async () => await publishing.DeleteNamespaceTransferSourceAsync(operation, deadline.Token));
                                verified = (await catalog.ReadNamespaceTransferAsync(operation, deadline.Token))!;
                                Assert.IsNotNull(verified.SourceDeletion); Assert.IsNull(verified.SourceDeletion.AcceptedAt);
                                await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(async () => await publishing.DeleteNamespaceTransferSourceAsync(operation, deadline.Token));
                            }
                            else
                            {
                                verified = await publishing.DeleteNamespaceTransferSourceAsync(operation, deadline.Token);
                                Assert.IsNotNull(verified.SourceDeletion!.AcceptedAt);
                                Assert.AreEqual(verified, await publishing.DeleteNamespaceTransferSourceAsync(operation, deadline.Token));
                            }
                            Assert.AreEqual(1, worker.Deletes); Assert.IsTrue(worker.SourceDeleted);
                            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                            if (!retireLocal)
                            {
                                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                                Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(verified.Plan.DeleteOperationId, deadline.Token));
                            }
                        }
                    }
                    finally
                    {
                        if (secondPublisher is not null) await secondPublisher.DisposeAsync();
                        if (recoveryWork is not null) await recoveryWork.DisposeAsync();
                    }
                    Assert.AreEqual(sameInstance && !deleteSource ? 2 : 0, publicationFaults);
                    Assert.AreEqual(1, worker.Uploads); Assert.AreEqual(deleteSource ? 1 : 0, worker.Deletes); Assert.AreEqual(0, provider.Reads);
                    if (retireLocal) Assert.IsFalse(File.Exists(sourcePath));
                    else CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                    string targetPath = Path.Combine(paths.SyncRootPath, "Archive", "moved.bin");
                    Assert.IsTrue(File.Exists(targetPath)); Assert.AreEqual(bytes.LongLength, new FileInfo(targetPath).Length);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(targetPath), deadline.Token));
                    Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, deadline.Token));
                    Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                    await using var input = await new MirrorPulseLocalTransferContentStore(paths).OpenLeaseAsync(prepared.Plan, deadline.Token);
                    CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input.Data.Name, deadline.Token));
                }
                if (retireLocal)
                {
                    await using var official = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath), deadline.Token);
                    await using var read = await official.BeginTransactionAsync(deadline.Token);
                    var originalItem = await read.Items.GetByItemIdAsync(retired!.TransferReceipt!.OfficialItemId, deadline.Token);
                    Assert.IsNotNull(originalItem); Assert.IsTrue(originalItem.IsTombstone);
                    Assert.AreEqual(nativeBefore.ItemId, originalItem.ItemId);
                    Assert.AreEqual(originalOfficialFileId, originalItem.LocalFileId);
                    var originalBinding = nativeBefore.LocalBinding ?? throw new AssertFailedException("The original native source binding was missing.");
                    Assert.AreEqual(new MirrorPulseLocalFileBinding(originalBinding.VolumeSerialNumber,
                        originalBinding.SyncRootFileId, originalBinding.LocalFileId), retired.TransferReceipt.LocalObject);
                    await read.RollbackAsync(deadline.Token);
                }
                await catalog.DisposeAsync();
                await using var nextCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var nextTransport = new MirrorPulseNormalUserWorkerTransport(nextRole, worker, worker, worker, worker, worker);
                var nextFactory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: nextCatalog, mayDispatch: _ => false,
                    scheduler: scheduler, namespaceExecution: nextRole, namespaceActor: actor, namespaceWork: work, transfers: Services(nextTransport));
                await using var reopened = nextFactory.Create(paths); await reopened.StartAsync(deadline.Token);
                int beforeOfflineRead = Volatile.Read(ref worker.RangeReads);
                string publishedTarget = Path.Combine(paths.SyncRootPath, "Archive", "moved.bin");
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(publishedTarget, deadline.Token));
                Assert.AreEqual(beforeOfflineRead, Volatile.Read(ref worker.RangeReads), "A restarted offline Host must retain all original target bytes.");
                var reopenedTarget = await reopened.InspectNamespaceMemberAsync("Archive/moved.bin", false, deadline.Token);
                Assert.AreEqual(verified.TargetPublication!.MemberId, reopenedTarget.MemberId);
                Assert.AreEqual(verified.TargetPublication.ItemId, reopenedTarget.NativeItemId);
                byte[] later = [7, 8, 9]; if (!retireLocal) await File.WriteAllBytesAsync(sourcePath, later, deadline.Token);
                byte[] targetLater = [11, 12, 13, 14]; await File.WriteAllBytesAsync(publishedTarget, targetLater, deadline.Token);
                Assert.AreEqual(verified, await reopened.PrepareNamespaceTransferAsync(operation, member.MemberId, target.MemberId, "moved.bin", deadline.Token));
                Assert.AreEqual(verified, await reopened.CopyNamespaceTransferAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.VerifyNamespaceTransferTargetAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.PublishNamespaceTransferTargetAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.ReadNamespaceTransferAsync(operation, deadline.Token));
                if (deleteSource)
                {
                    if (retireLocal) Assert.AreEqual(retired, await reopened.RetireNamespaceTransferSourceAsync(operation, deadline.Token));
                    else if (sameInstance) await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(async () => await reopened.DeleteNamespaceTransferSourceAsync(operation, deadline.Token));
                    else Assert.AreEqual(verified, await reopened.DeleteNamespaceTransferSourceAsync(operation, deadline.Token));
                }
                Assert.AreEqual(1, worker.Uploads); Assert.AreEqual(deleteSource ? 1 : 0, worker.Deletes);
                if (retireLocal) Assert.IsFalse(File.Exists(sourcePath));
                else CollectionAssert.AreEqual(later, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                CollectionAssert.AreEqual(targetLater, await File.ReadAllBytesAsync(publishedTarget, deadline.Token));
                Assert.HasCount(2, await nextCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                if (!retireLocal) TestContext.WriteLine($"HostTransfer: sameInstance={sameInstance}; originalBinding=True; privateWholeBytes=True; originalCopyReceipt=True; targetWholeReadback=True; distinctTargetBirthPrepared=True; nativeTargetPublished=True; onlineOnlyPublication=True; nativeTargetWholeBytesVerified=True; targetRangeRpcReads={Volatile.Read(ref worker.RangeReads)}; offlineReopenWholeBytes=True; nativeIdentityAndDaclRetained=True; closedRootReopened=False; namespaceDeleteDenied=True; sdkProjectionFailure={projectionFault.Failures}; knownBindingRecovery={!deleteSource}; registrationResponseLoss={sameInstance && !deleteSource}; publicationWriteFailure={sameInstance && !deleteSource}; normalUserRpc=True; sourceRetired=False; bothFences=True; newRoleHistoricalReplay=True; laterSourceAndTargetEditsRetained=True; actualWorkerProcess=False; controlCliIntegrated=False.");
                if (deleteSource && !retireLocal) TestContext.WriteLine($"HostTransferSourceDelete: sameInstance={sameInstance}; originalConditionalRevision=True; existingWriterRejected=True; writableMappingInputClosedRejected=True; queuedWriterRejectedDuringRpc=True; remoteDeleted=True; acceptanceRetained={!sameInstance}; unknownOutcomeNotRedispatched={sameInstance}; localSourceRetired=False; originalJournalAcknowledged=False; bothFences=True; normalUserRpc=True; actualWorkerProcess=False; controlCliIntegrated=False.");
                if (retireLocal) TestContext.WriteLine($"HostTransferSourceRetirement: sameInstance={sameInstance}; originalSdkItemTombstone=True; originalNativeBinding=True; sameDataGuardThroughRemoteAndLocalDelete=True; originalSourceNameSettled=True; privateWholeBytesRetained=True; targetWholeBytesRetained=True; existingWriterAndMappingRejected=True; queuedWriterRejectedDuringRpc=True; remoteDeleteCalls=1; newRoleOfflineCompletedReplay=True; laterTargetEditsRetained=True; bothFences=True; originalJournalAcknowledged=False; actualWorkerProcess=False; controlCliIntegrated=False.");
                if (retirementFault is not null) TestContext.WriteLine($"HostTransferSourceRetirementRecovery: fault={retirementFault}; sameInstance={sameInstance}; originalStartAndItemRetained=True; nativeDeleteCalls={nativeDeleteCalls}; remoteDeleteCalls={worker.Deletes}; nameAndBothFencesRetainedBeforeSettlement=True; firstReceiptImmutable=True; sameRoleStoreOwnerReopened=True; unfinishedRoleRotated=False; privateWholeBytesRetained=True; originalJournalAcknowledged=False.");
            }
            finally
            {
                registry.Unregister(paths.SyncRootPath);
                RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
                RestoreTransferArchiveForCleanup(paths.SyncRootPath, caller.User!);
                DeleteFixture(directory);
            }
        }
    }

    [TestMethod]
    public async Task TransferFixtureHydrationReadsTheAcceptedPublicIdentityThroughTheProductionDemandProvider()
    {
        var target = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.transfer-hydration"), InstanceId.New(),
            new("archive", "Archive", "Archive", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N")), [target]);
        using var caller = WindowsIdentity.GetCurrent(); await using var role = new MirrorPulseNamespaceExecutionSession();
        byte[] bytes = [5, 7, 11, 13];
        var identity = router.CreateFileIdentity(target.InstanceId, target.UniquenessKey, "target:original", "target-v1");
        var worker = new TransferNativeWorker(role, caller.User!, target, bytes) { HydrationIdentity = identity };
        var transport = new MirrorPulseNormalUserWorkerTransport(role, worker, worker, worker, worker, worker);
        var provider = new MirrorPulseDemandProvider(router, transport);
        await using var content = await provider.OpenReadAsync("Archive/moved.bin", identity.Encode(), bytes.LongLength, 0, bytes.LongLength);
        byte[] received = new byte[bytes.Length]; await content.ReadExactlyAsync(received);
        CollectionAssert.AreEqual(bytes, received); Assert.AreEqual(1, worker.RangeReads);
    }

    private static async Task PrepareTransferAcceptedSourceAsync(CloudFile file, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, MirrorPulseNamespaceActor actor,
        RootRegistration root, byte[] bytes, CancellationToken token)
    {
        var confirmation = new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role);
        var before = await file.InspectAsync(token);
        var binding = await confirmation.CaptureUploadBindingAsync(file, before, token);
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), root.InstanceId, root.UniquenessKey,
            MirrorPulseWorkerChangeKind.ContentUpdate, "original.bin", null, false, before.RemoteRevision,
            bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), MirrorPulseMutationOrigin.Journal, binding);
        await catalog.PrepareMutationAsync(intent, token);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing, cancellationToken: token);
        // Original source upload acceptance is a fixture premise. The public native confirmation,
        // retained identity, guarded source-delete producer and actual Worker call are exercised here.
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "source-v1", token);
        var identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "source:accepted-original", "source-v1");
        var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, binding, identity.ItemId, identity.RemoteId,
            "source-v1", bytes.LongLength, intent.ContentSha256!);
        await catalog.SaveContentAcceptanceProofAsync(proof, token);
        var receipt = await confirmation.ConfirmAsync(file, proof, token);
        Assert.IsTrue(receipt.MayAcknowledge, $"Source confirmation requires its actual native/public acceptance: {receipt.Outcome}.");
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged, cancellationToken: token);
    }

    private static async Task ExecuteTransferPublicationFixtureSqlAsync(MirrorPulseStoragePaths paths, string sql, CancellationToken token)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = paths.ProductCatalogDatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(token); await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static void RestoreTransferArchiveForCleanup(string syncRoot, SecurityIdentifier user)
    {
        // The preceding source-context helper validates the exact GUID, marker and sync-root path.
        var archive = new DirectoryInfo(Path.Combine(syncRoot, "Archive"));
        if (!archive.Exists) return;
        if (archive.LinkTarget is not null) throw new InvalidOperationException("Transfer fixture cleanup cannot follow a directory link.");
        var access = new DirectorySecurity(); access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        archive.SetAccessControl(access);
    }

    private sealed class TransferNativeWorker(MirrorPulseNamespaceExecutionSession role, SecurityIdentifier owner,
        RootRegistration target, byte[] bytes) : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerMutationTransport
    {
        internal MirrorPulseNamespaceTransferPlan? Plan;
        internal int Uploads;
        internal int Deletes;
        internal int RangeReads;
        internal bool SourceDeleted;
        internal bool LoseDeleteReply;
        internal Action? DeleteInspection;
        internal CloudPlaceholderIdentity? HydrationIdentity;
        private bool IsSource(InstanceId instance, string? root, string path) => Plan is { } plan &&
            instance == plan.SourceRoute.InstanceId && root == plan.SourceRoute.RootKey && path == "original.bin";
        private void AssertNormalUser()
        {
            using var identity = WindowsIdentity.GetCurrent();
            Assert.AreEqual(owner, identity.User); Assert.IsFalse(identity.Groups?.Contains(role.RoleSid) ?? false);
        }
        private void RequireNormalUser(InstanceId instance, string? root, string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            Assert.AreEqual(owner, identity.User); Assert.IsFalse(identity.Groups?.Contains(role.RoleSid) ?? false);
            Assert.AreEqual(target.InstanceId, instance); Assert.AreEqual(target.UniquenessKey, root); Assert.AreEqual("moved.bin", path);
        }
        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            RequireNormalUser(request.InstanceId, request.RootKey, request.NormalizedPath); Uploads++;
            Assert.IsNotNull(Plan); Assert.AreEqual(Plan.CopyOperationId, request.OperationId);
            Assert.IsTrue(request.RequireConditionalTargetSupport && request.DestinationMustBeAbsent); Assert.IsNull(request.ExpectedRevision);
            using var content = new MemoryStream(); await request.Content.CopyToAsync(content, token);
            CollectionAssert.AreEqual(bytes, content.ToArray()); return "target-v1";
        }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        {
            if (IsSource(request.InstanceId, request.RootKey, request.NormalizedPath))
            {
                AssertNormalUser();
                return ValueTask.FromResult(SourceDeleted ? null : "source-v1");
            }
            RequireNormalUser(request.InstanceId, request.RootKey, request.NormalizedPath); return ValueTask.FromResult<string?>("target-v1");
        }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            RequireNormalUser(request.InstanceId, request.RootKey, request.NormalizedPath);
            if (request.ExpectedRevision is { } revision) Assert.AreEqual("target-v1", revision);
            else
            {
                // Real hydration carries its accepted public identity. The explicit conditional
                // revision belongs to the separate whole-target verification transport request.
                Assert.IsNotNull(HydrationIdentity);
                var identity = CloudPlaceholderIdentity.Decode(request.FileIdentity.Span);
                Assert.AreEqual(HydrationIdentity.ItemId, identity.ItemId);
                Assert.AreEqual(HydrationIdentity.RemoteId, identity.RemoteId);
                Assert.AreEqual("target-v1", identity.RemoteRevision);
            }
            Interlocked.Increment(ref RangeReads);
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes.AsSpan(checked((int)request.Offset), checked((int)request.Length)).ToArray(), writable: false));
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            if (IsSource(request.InstanceId, request.RootKey, "original.bin"))
            {
                AssertNormalUser(); Assert.AreEqual(string.Empty, request.NormalizedPath);
                return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new("source:accepted-original", "source-v1", "File", "original.bin", bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
            }
            RequireNormalUser(request.InstanceId, request.RootKey, "moved.bin"); Assert.AreEqual(string.Empty, request.NormalizedPath);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new("target:original", "target-v1", "File", "moved.bin", bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        {
            using var identity = WindowsIdentity.GetCurrent(); Assert.AreEqual(owner, identity.User);
            Assert.IsFalse(identity.Groups?.Contains(role.RoleSid) ?? false);
            Assert.IsTrue(IsSource(request.InstanceId, request.RootKey, request.NormalizedPath));
            Assert.AreEqual(Plan!.DeleteOperationId, request.OperationId); Assert.AreEqual("source-v1", request.ExpectedRevision); Assert.IsFalse(request.IsDirectory);
            Deletes++; DeleteInspection?.Invoke(); SourceDeleted = true;
            if (LoseDeleteReply) throw new IOException("Synthetic response loss after the original conditional source delete.");
            return ValueTask.FromResult<string?>(null);
        }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) =>
            throw new AssertFailedException("A transfer is not a same-root Worker move.");
    }
}
