using System.Security.AccessControl;
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
    public async Task NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification()
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
            byte[] bytes = sameInstance ? [] : new byte[2_097_409]; new Random(517).NextBytes(bytes);
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
                var nativeBefore = await fileSystem.GetFile("Docs/original.bin").InspectAsync(deadline.Token);
                await fileSystem.DisposeAsync();
                var worker = new TransferNativeWorker(role, caller.User!, targetRoot, bytes);
                var transport = new MirrorPulseNormalUserWorkerTransport(role, worker, worker, worker, worker, worker);
                int targetProtocol = 1;
                MirrorPulseTransferWorkerServices Services(MirrorPulseNormalUserWorkerTransport source) =>
                    new(source, source, source, source, id => id == targetRoot.InstanceId ? targetProtocol : 2);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: catalog, mayDispatch: _ => true,
                    scheduler: scheduler, namespaceExecution: role, namespaceActor: actor, namespaceWork: work, transfers: Services(transport));
                Guid operation = Guid.NewGuid(); MirrorPulseNamespaceTransferCopy verified;
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
                    Assert.AreEqual(targetIdentity.ItemId, verified.TargetBirth.Plan.ItemId);
                    Assert.AreEqual(targetIdentity.RemoteId, verified.TargetBirth.Plan.RemoteId);
                    Assert.AreNotEqual(prepared.Plan.Source.Identity.ItemId, verified.TargetBirth.Plan.ItemId);
                    Assert.AreEqual(MirrorPulseNamespaceBirthOrigin.TransferTarget, verified.TargetBirth.Birth.Origin);
                    Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(verified.TargetBirth.Birth.OperationId, deadline.Token));
                    Assert.AreEqual(verified, await runtime.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token));
                    Assert.AreEqual(1, worker.Uploads); Assert.AreEqual(0, worker.Deletes); Assert.AreEqual(0, provider.Reads);
                    CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                    Assert.IsFalse(File.Exists(Path.Combine(paths.SyncRootPath, "Archive", "moved.bin")));
                    Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, deadline.Token));
                    Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                    await using var input = await new MirrorPulseLocalTransferContentStore(paths).OpenLeaseAsync(prepared.Plan, deadline.Token);
                    CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input.Data.Name, deadline.Token));
                }
                await catalog.DisposeAsync();
                await using var nextCatalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var nextTransport = new MirrorPulseNormalUserWorkerTransport(nextRole, worker, worker, worker, worker, worker);
                var nextFactory = new CfSharpMirrorPulseCloudRuntimeFactory(router: router, catalog: nextCatalog, mayDispatch: _ => false,
                    scheduler: scheduler, namespaceExecution: nextRole, namespaceActor: actor, namespaceWork: work, transfers: Services(nextTransport));
                await using var reopened = nextFactory.Create(paths); await reopened.StartAsync(deadline.Token);
                byte[] later = [7, 8, 9]; await File.WriteAllBytesAsync(sourcePath, later, deadline.Token);
                Assert.AreEqual(verified, await reopened.PrepareNamespaceTransferAsync(operation, member.MemberId, target.MemberId, "moved.bin", deadline.Token));
                Assert.AreEqual(verified, await reopened.CopyNamespaceTransferAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.VerifyNamespaceTransferTargetAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.PrepareNamespaceTransferTargetBirthAsync(operation, deadline.Token));
                Assert.AreEqual(verified, await reopened.ReadNamespaceTransferAsync(operation, deadline.Token));
                Assert.AreEqual(1, worker.Uploads); Assert.AreEqual(0, worker.Deletes);
                CollectionAssert.AreEqual(later, await File.ReadAllBytesAsync(sourcePath, deadline.Token));
                Assert.HasCount(2, await nextCatalog.ReadNamespaceDispatchFencedRootsAsync(deadline.Token));
                TestContext.WriteLine($"HostTransfer: sameInstance={sameInstance}; originalBinding=True; privateWholeBytes=True; originalCopyReceipt=True; targetWholeReadback=True; distinctTargetBirthPrepared=True; nativeBirthStarted=False; normalUserRpc=True; sourceRetired=False; nativeTargetPublished=False; bothFences=True; owners=2; newRoleHistoricalReplay=True; actualWorkerProcess=False; controlCliIntegrated=False.");
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
        { RequireNormalUser(request.InstanceId, request.RootKey, request.NormalizedPath); return ValueTask.FromResult<string?>("target-v1"); }
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            RequireNormalUser(request.InstanceId, request.RootKey, request.NormalizedPath); Assert.AreEqual("target-v1", request.ExpectedRevision);
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes.AsSpan(checked((int)request.Offset), checked((int)request.Length)).ToArray(), writable: false));
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            RequireNormalUser(request.InstanceId, request.RootKey, "moved.bin"); Assert.AreEqual(string.Empty, request.NormalizedPath);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new("target:original", "target-v1", "File", "moved.bin", bytes.LongLength, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        { Deletes++; throw new AssertFailedException("Copy and target verification cannot retire the source."); }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) =>
            throw new AssertFailedException("A transfer is not a same-root Worker move.");
    }
}
