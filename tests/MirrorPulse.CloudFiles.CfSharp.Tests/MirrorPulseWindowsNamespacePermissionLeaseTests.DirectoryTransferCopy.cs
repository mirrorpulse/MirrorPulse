using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    public async Task NativeDirectoryTransferPublishesVerifiedTargetAndRetainsOriginalSource()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable native Windows environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var adapter = AdapterId.Parse("example.directory-transfer-copy");
        var sourceRoot = AdapterRootRegistrationMapper.Map(adapter, InstanceId.New(), new("docs", "Docs", "Docs", false),
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var targetRoot = AdapterRootRegistrationMapper.Map(adapter, InstanceId.New(), new("archive", "Archive", "Archive", false),
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [sourceRoot, targetRoot]);
        using var caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        await using var actor = new MirrorPulseNamespaceActor();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var work = new MirrorPulseNamespaceWorkAdmission([sourceRoot.RootId, targetRoot.RootId]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        byte[] bytes = [17, 19, 23, 29, 31];
        bool registered = false;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [3, 1, 4]));
            registered = true;
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), "", token);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                .WithContentProvider(new ProtectedPermissionNoSourceProvider()).Build();
            await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(token));
            Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Docs"));
            Directory.CreateDirectory(Path.Combine(paths.SyncRootPath, "Archive"));
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetDirectory("Archive") })
            {
                SetProtectedFixtureUserOwner(item, caller.User!);
                await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token);
                var observed = await metadata.InspectAsync(token);
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), observed.RootId, observed.LocalObject,
                    observed.RelativePath, observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
                originals.Add(new(baseline, new(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject, baseline.RootId,
                    baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value,
                    baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, true), DateTimeOffset.UtcNow)));
            }
            await catalog.PrepareNamespacePermissionChangesAsync(originals, token);
            await using (var protection = new MirrorPulseNamespacePermissionCoordinator(catalog))
            {
                await role.RunNamespaceOperationAsync(async () =>
                {
                    foreach (var original in originals.AsEnumerable().Reverse())
                        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                            (await protection.ApplyProtectedAsync(original.Intent.OperationId,
                                NativeOriginalMemberItem(fileSystem, original.Baseline), router, null, token)).Permission!.Outcome);
                });
            }
            var manifest = new AdapterManifest(1, adapter, "Fixture", "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
            var installed = new InstalledAdapter(manifest, InstallId.New(), paths.DataRootPath, new(new string('A', 64)),
                AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            AdapterInstance Instance(RootRegistration root) => new(adapter, installed.InstallId, root.InstanceId, root.Label,
                new Dictionary<string, string>(), [], Path.Combine(paths.DataRootPath, "files"),
                Path.Combine(paths.DataRootPath, "transfer-cache"), true,
                AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
            await catalog.SaveAdapterTopologyAsync(new([installed], [Instance(sourceRoot), Instance(targetRoot)], [sourceRoot, targetRoot]), token);
            var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
            await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, token);
            var sourceParent = members.Values.Single(member => member.RelativePath == "Docs");
            var targetParent = members.Values.Single(member => member.RelativePath == "Archive");
            await using var feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(token);
            var creation = new MirrorPulseNamespaceCreationCoordinator(fileSystem, state, catalog, router, role, actor);
            var folder = await creation.CreateAsync(Guid.NewGuid(), sourceParent.MemberId, "folder", true, token);
            var payload = await creation.CreateAsync(Guid.NewGuid(), folder.MemberId, "payload.bin", false, token);
            string sourcePath = Path.Combine(paths.SyncRootPath, payload.RelativePath);
            await File.WriteAllBytesAsync(sourcePath, bytes, token);
            var source = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(folder.MemberId, token))!;
            var target = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(targetParent.MemberId, token))!;
            var coordinator = new MirrorPulseNamespaceTransferCoordinator(fileSystem, state, catalog, router, role, actor,
                new MirrorPulseLocalTransferContentStore(paths), _ => true, _ => 2)
            { JournalFeed = feed };
            await coordinator.MaterializeDirectorySourceAsync(source, target, token);
            await using var roots = await work.EnterPendingOperationAsync([sourceRoot.RootId, targetRoot.RootId], token);
            await using var instances = await scheduler.AcquireAsync([sourceRoot.InstanceId, targetRoot.InstanceId], token);
            Guid operationId = Guid.NewGuid();
            var plan = await coordinator.PrepareDirectoryAsync(operationId, source, target, "copied", token);
            Assert.HasCount(2, plan.Members);
            Assert.IsTrue(plan.Members.All(member => member.SourceRevision is null));
            var remote = new NativeDirectoryCopyWorker(role, caller.User!, targetRoot, sourcePath, bytes, plan);
            var transport = new MirrorPulseNormalUserWorkerTransport(role, remote, remote, remote, remote, remote);
            var workers = new MirrorPulseTransferWorkerServices(transport, transport, transport, transport, _ => 2, transport);
            var result = await coordinator.CopyDirectoryAsync(operationId, workers, token);
            Assert.IsTrue(result.Steps.All(step => step.AcceptedAt is not null));
            var verification = await coordinator.VerifyDirectoryTargetAsync(operationId, workers, token);
            Assert.AreEqual("target-directory-after-children", verification.Members[0].Revision);
            Assert.AreEqual("uploaded-file", verification.Members[1].Revision);
            Assert.AreEqual(1, remote.Reads);
            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, token));
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.SyncRootPath, "Archive", "copied")));
            Assert.AreEqual(1, remote.Creates);
            Assert.AreEqual(1, remote.Uploads);
            _ = await coordinator.CopyDirectoryAsync(operationId, workers, token);
            Assert.AreEqual(1, remote.Creates);
            Assert.AreEqual(1, remote.Uploads);

            var published = await coordinator.PublishDirectoryTargetAsync(operationId, workers, token);
            Assert.HasCount(2, published);
            Assert.AreEqual(published[0].Protection.Context.Member.MemberId, published[1].Protection.Context.Member.ParentMemberId);
            Assert.AreEqual("Archive/copied/payload.bin", published[1].Protection.Context.Location.RelativePath);
            var targetFile = await fileSystem.GetFile("Archive/copied/payload.bin").InspectAsync(token);
            Assert.AreEqual(CloudSynchronizationState.InSync, targetFile.SynchronizationState);
            Assert.AreEqual("uploaded-file", targetFile.RemoteRevision);
            Assert.AreNotEqual(payload.LocalObject, published[1].Protection.Context.Member.LocalObject);
            var replayed = await coordinator.PublishDirectoryTargetAsync(operationId, workers, token);
            CollectionAssert.AreEqual(published.Select(item => item.Protection.Context.Member).ToArray(),
                replayed.Select(item => item.Protection.Context.Member).ToArray());
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath, token));
            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync(token));
            Assert.AreEqual(1, remote.Creates);
            Assert.AreEqual(1, remote.Uploads);
        }
        finally
        {
            if (registered) registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            RestoreControlledCreationFixtureForCleanup(paths.SyncRootPath, caller.User!);
            RestoreTransferArchiveForCleanup(paths.SyncRootPath, caller.User!);
            var payload = new FileInfo(Path.Combine(paths.SyncRootPath, "Docs", "folder", "payload.bin"));
            if (payload.Exists)
            {
                var access = new FileSecurity();
                access.SetAccessRuleProtection(true, false);
                access.AddAccessRule(new FileSystemAccessRule(caller.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                payload.SetAccessControl(access);
            }
            DeleteFixture(directory);
        }
    }

    private sealed class NativeDirectoryCopyWorker(MirrorPulseNamespaceExecutionSession role, SecurityIdentifier owner,
        RootRegistration target, string sourcePath, byte[] bytes, MirrorPulseDirectoryTransferAdmission plan)
        : IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
          IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        internal int Creates;
        internal int Uploads;
        internal int Reads;

        private void RequireNormalUser()
        {
            using var identity = WindowsIdentity.GetCurrent();
            Assert.AreEqual(owner, identity.User);
            Assert.IsFalse(identity.Groups?.Contains(role.RoleSid) ?? false);
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var write = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            });
        }

        public ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request, CancellationToken token)
        {
            RequireNormalUser();
            Assert.AreEqual(target.InstanceId, request.InstanceId);
            Assert.AreEqual("copied", request.NormalizedPath);
            Assert.AreEqual(plan.Members[0].CopyOperationId, request.OperationId);
            Assert.IsTrue(request.MustBeAbsent);
            Creates++;
            return ValueTask.FromResult("created-directory");
        }

        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token)
        {
            RequireNormalUser();
            Assert.AreEqual(1, Creates);
            Assert.AreEqual(target.InstanceId, request.InstanceId);
            Assert.AreEqual("copied/payload.bin", request.NormalizedPath);
            Assert.AreEqual(plan.Members[1].CopyOperationId, request.OperationId);
            Assert.IsTrue(request.RequireConditionalTargetSupport);
            using var received = new MemoryStream();
            await request.Content.CopyToAsync(received, token);
            CollectionAssert.AreEqual(bytes, received.ToArray());
            Uploads++;
            return "uploaded-file";
        }

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token)
        {
            RequireNormalUser();
            Assert.AreEqual(target.InstanceId, request.InstanceId);
            return ValueTask.FromResult<string?>(request.NormalizedPath == "copied" ? "target-directory-after-children" : "uploaded-file");
        }

        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            RequireNormalUser();
            Assert.AreEqual("uploaded-file", request.ExpectedRevision);
            Assert.AreEqual("copied/payload.bin", request.NormalizedPath);
            Reads++;
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            RequireNormalUser();
            var directory = new MirrorPulseWorkerDirectoryEntry("target-directory", "target-directory-after-children",
                "Directory", "copied", null, null, null, false);
            var file = new MirrorPulseWorkerDirectoryEntry("target-file", "uploaded-file", "File",
                "copied/payload.bin", bytes.LongLength, null, null, false);
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(request.NormalizedPath == "" ? [directory] : [file],
                ReadOnlyMemory<byte>.Empty, true));
        }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token) => throw new AssertFailedException("Unexpected deletion.");
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token) => throw new AssertFailedException("Unexpected Move.");
    }
}
