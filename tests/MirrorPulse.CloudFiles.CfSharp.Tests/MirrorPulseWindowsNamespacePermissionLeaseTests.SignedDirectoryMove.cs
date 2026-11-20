using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedAtomicDirectory")]
    public async Task NativeProductionDirectoryMoveSettlesTheOriginalThroughInstalledLocalWorker()
    {
        string? package = Environment.GetEnvironmentVariable("MP_SIGNED_LOCAL_PACKAGE");
        string? executable = Environment.GetEnvironmentVariable("MP_S06_ATOMIC_LOCAL_WORKER_EXE");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" || (package is null && executable is null))
            Assert.Inconclusive("Requires the disposable native Windows environment and a Local Worker.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        string cache = Path.Combine(paths.DataRootPath, "transfer-cache");
        string backend = Path.Combine(cache, "temporary", "configured-storage");
        const string source = "tree";
        const string destination = "moved";
        const string child = "deep";
        const string fileName = "leaf.bin";
        const string originalFile = source + "/" + child + "/" + fileName;
        const string movedFile = destination + "/" + child + "/" + fileName;
        string[] originals = [source, source + "/" + child, originalFile];
        byte[] bytes = [76, 111, 99, 97, 108, 13, 10];
        byte[] laterBytes = [.. bytes, 42, 43];
        Directory.CreateDirectory(Path.Combine(backend, source, child));
        await File.WriteAllBytesAsync(Path.Combine(backend, originalFile), bytes, token);
        var topology = await MirrorPulseAtomicDirectoryJournalTests.PrepareDirectoryJournalTopologyAsync(paths, package, executable, backend, cache, token);
        var root = topology.Roots.Single();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        Exception? operationFailure = null;
        bool registered = false;
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = caller.User ?? throw new InvalidOperationException("The native fixture has no Windows owner.");
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            registered = true;
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, token);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            await catalog.SaveAdapterTopologyAsync(topology, token);
            await using var worker = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await worker.StartAsync(topology);
            while (!worker.IsConnected(root.InstanceId)) await Task.Delay(25, token);
            Assert.IsTrue(worker.SupportsDurableDirectoryMove(root.InstanceId), "The installed Worker must negotiate durable directory Move.");
            var originalBindings = await SeedAsync(worker);
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var actor = new MirrorPulseNamespaceActor();
            await using var scheduler = new MirrorPulseInstanceScheduler();
            await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
            var laterSource = new LaterDirectoryMoveSourceReadback(worker, backend, movedFile, laterBytes);
            var transport = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, laterSource, worker);
            var noContent = new RemoteMovementContentGuard();
            int dispatchEnabled = 1;
            CloudLocalChangeFeed? journal = null;
            var projection = new MirrorPulseTransferProjectionCommitFault(execution.RoleSid);
            DisabledCreationStoreObserver? store = null;
            var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: noContent, uploads: transport, stats: transport,
                mutations: transport, router: router, catalog: catalog,
                mayDispatch: instance => Volatile.Read(ref dispatchEnabled) != 0 && worker.IsConnected(instance), scheduler: scheduler,
                namespaceExecution: execution, namespaceActor: actor, namespaceWork: work, initializeNamespaceOnStart: true)
            {
                StateStoreDecorator = inner => store = new DisabledCreationStoreObserver(projection.Wrap(inner)),
                TransferJournalReady = feed => journal = feed
            };
            await using var runtime = factory.Create(paths);
            await runtime.StartAsync(token);
            AssertRecursiveJournalStartup(runtime, root.RootId);
            Assert.IsNotNull(store);
            var parent = await runtime.InspectNamespaceMemberAsync("Docs", true, token);
            var member = await runtime.InspectNamespaceMemberAsync("Docs/" + source, true, token);
            Guid localOperation = Guid.NewGuid();
            await runtime.PrepareNamespaceMemberMoveAsync(localOperation, member.MemberId, parent.MemberId, destination, token);
            var rootIdentity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, destination, originalBindings[0].Revision);
            projection.Arm(rootIdentity.ItemId);
            await runtime.ExecuteNamespaceMemberMoveAsync(localOperation, token);
            Guid originalOperation;
            while (true)
            {
                var pending = await catalog.ReadPendingJournalDirectoryMovesAsync(null, 1, token);
                if (pending.Count == 1) { originalOperation = pending[0].OperationId; break; }
                await Task.Delay(25, token);
            }
            MirrorPulseJournalDirectoryMove? retained;
            do
            {
                retained = await catalog.ReadJournalDirectoryMoveAsync(originalOperation, token);
                if (retained?.Mutation.State != MirrorPulseMutationState.Acknowledged) await Task.Delay(50, token);
            }
            while (retained?.Mutation.State != MirrorPulseMutationState.Acknowledged);

            Assert.AreEqual(localOperation, retained.Admission.LocalMoveOperationId);
            Assert.AreEqual(originalOperation, retained.Admission.JournalOperationId);
            Assert.AreEqual(originals.Length, retained.ProjectedMemberCount);
            Assert.AreEqual(1, projection.Failures);
            Assert.AreEqual(0, noContent.Reads);
            Assert.IsFalse(Directory.Exists(Path.Combine(backend, source)));
            CollectionAssert.AreEqual(laterBytes, await File.ReadAllBytesAsync(Path.Combine(backend, movedFile), token));
            Assert.AreEqual(1, laterSource.Edits);
            Assert.IsNotNull(laterSource.Revision);
            Assert.AreNotEqual(originalBindings[^1].Revision, laterSource.Revision);
            await using (var official = await store.Store.BeginTransactionAsync(token))
            {
                Assert.IsNull(await official.Operations.GetAsync(originalOperation, token));
                for (int index = 0; index < retained.Admission.Members.Count; index++)
                {
                    var original = retained.Admission.Members[index];
                    var target = retained.Targets![index];
                    var entry = originalBindings.Single(value => value.Binding == original.Binding);
                    var row = await official.Items.GetByItemIdAsync(target.ItemId, token);
                    Assert.IsNotNull(row);
                    Assert.AreEqual(original.TargetPath, row.RelativePath.Replace('\\', '/'));
                    Assert.AreEqual(expected: entry.Revision, actual: row.RemoteRevision);
                }
                await official.RollbackAsync(token);
            }
            for (int index = 0; index < retained.Admission.Members.Count; index++)
            {
                var original = retained.Admission.Members[index];
                var native = await runtime.InspectNamespaceMemberAsync(original.TargetPath, original.IsDirectory, token);
                Assert.AreEqual(original.MemberId, native.MemberId);
                Assert.AreEqual(retained.Targets![index].ItemId, native.NativeItemId);
            }
            Assert.IsNotNull(journal);
            Volatile.Write(ref dispatchEnabled, 0);
            var bornDirectory = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), parent.MemberId, "created", true, token);
            var bornNested = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), bornDirectory.MemberId, "deep", true, token);
            string importFile = Path.Combine(directory, "recursive-create-input.bin");
            await File.WriteAllBytesAsync(importFile, bytes, token);
            Guid importOperation = Guid.NewGuid();
            await runtime.PrepareNamespaceImportAsync(importOperation, bornNested.MemberId, fileName, importFile, token);
            var bornFile = await runtime.ExecuteNamespaceImportAsync(importOperation, token);
            MirrorPulseNamespaceMemberRegistration[] bornMembers = [bornDirectory, bornNested, bornFile];
            string[] bornPaths = ["Docs/created", "Docs/created/deep", "Docs/created/deep/" + fileName];
            CloudLocalChange[] createOriginals;
            do
            {
                var batch = await journal.ReadBatchAsync(token);
                Assert.IsFalse(batch.RequiresFullRescan);
                createOriginals = batch.Changes.Where(change => change.Kind == CloudLocalChangeKind.Create &&
                    bornPaths.Contains(change.RelativePath.Replace('\\', '/'), StringComparer.Ordinal)).ToArray();
                if (createOriginals.Length != bornMembers.Length) await Task.Delay(25, token);
            }
            while (createOriginals.Length != bornMembers.Length);
            foreach (var original in createOriginals)
                Assert.IsNull(await catalog.ReadMutationAsync(original.OperationId, token));
            Assert.IsFalse(Directory.Exists(Path.Combine(backend, "created")));
            Volatile.Write(ref dispatchEnabled, 1);
            bool createPending;
            do
            {
                await using var current = await store.Store.BeginTransactionAsync(token);
                createPending = false;
                foreach (var original in createOriginals)
                    createPending |= await current.Operations.GetAsync(original.OperationId, token) is not null;
                await current.RollbackAsync(token);
                if (createPending) await Task.Delay(25, token);
            }
            while (createPending);
            Assert.IsTrue(Directory.Exists(Path.Combine(backend, "created/deep")));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(backend, "created/deep/" + fileName), token));
            foreach (var born in bornMembers)
            {
                Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, born.Origin);
                var location = await catalog.ReadNamespaceMemberLocationAsync(born.MemberId, token);
                var identity = await catalog.ReadNamespaceMemberIdentityAsync(born.MemberId, token);
                var native = await runtime.InspectNamespaceMemberAsync(location.RelativePath, born.IsDirectory, token);
                Assert.AreEqual(born.MemberId, native.MemberId);
                Assert.AreEqual(identity.ItemId, native.NativeItemId);
                Assert.IsNotNull(identity.AcceptanceOperationId);
            }
            Assert.AreEqual(0, noContent.Reads);
            if (package is not null)
            {
                await using var packageStream = File.OpenRead(package);
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, token));
                TestContext.WriteLine($"SignedDirectoryMove: packageSha256={digest}; originalCount=1; members=3; hydrationReads=0; interruptedProjection=1; createdMembers=3; createOriginals=3; createdFileBytes={bytes.Length}; laterSourceContentPreserved=True.");
            }
        }
        catch (Exception error) { operationFailure = error; throw; }
        finally
        {
            try
            {
                if (registered) await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, owner,
                    ["Docs", "Docs/tree", "Docs/tree/deep", "Docs/moved", "Docs/moved/deep", "Docs/created", "Docs/created/deep"],
                    ["Docs/" + originalFile, "Docs/" + movedFile, "Docs/created/deep/" + fileName]);
            }
            catch when (operationFailure is not null) { TestContext.WriteLine("The disposed fixture also reported a cleanup failure."); }
            finally { if (registered) registry.Unregister(paths.SyncRootPath); }
            if (operationFailure is null) DeleteFixture(directory);
            else
            {
                try { DeleteFixture(directory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    TestContext.WriteLine("The disposed fixture also reported a final cleanup failure.");
                }
            }
        }

        async Task<IReadOnlyList<(MirrorPulseLocalFileBinding Binding, string Revision)>> SeedAsync(AdapterInstanceProcessSupervisor supervisor)
        {
            var seedState = new MirrorPulseCfSharpStateSession(paths);
            await using var sdk = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(seedState)
                .WithContentProvider(new RemoteMovementContentGuard()).Build();
            await sdk.StartAsync(token);
            (await sdk.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: token)).ThrowIfAnyFailed();
            var changes = new List<CloudRemoteChange>();
            foreach (string relative in originals)
            {
                bool isDirectory = relative != originalFile;
                string? revision = await supervisor.StatAsync(new(root.InstanceId, relative, root.UniquenessKey), token);
                Assert.IsNotNull(revision);
                var identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, relative, revision);
                var metadata = isDirectory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
                changes.Add(new("seed-" + relative, isDirectory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                    identity.RemoteId, revision, isDirectory ? CloudItemKind.Directory : CloudItemKind.File, "Docs/" + relative, identity.ItemId,
                    length: isDirectory ? null : bytes.Length, metadata: metadata, cursorAfter: new byte[] { (byte)(changes.Count + 1) }));
            }
            var result = await sdk.ApplyRemoteChangesAsync(new CloudRemoteChangeBatch(root.InstanceId + "/directory-move", ReadOnlyMemory<byte>.Empty,
                changes, new byte[] { 3 }), cancellationToken: token);
            Assert.AreEqual(CloudRemoteBatchStatus.Applied, result.Status);
            Assert.AreEqual(originals.Length, result.AppliedEntryCount);
            foreach (string folder in new[] { "Docs", "Docs/tree", "Docs/tree/deep" })
                await sdk.GetDirectory(folder).SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: token);
            SetProtectedFixtureUserOwner(sdk.Root, owner);
            SetProtectedFixtureUserOwner(sdk.GetDirectory("Docs"), owner);
            var retained = new List<(MirrorPulseLocalFileBinding Binding, string Revision)>();
            foreach (var change in changes)
            {
                CloudItem item = change.ItemKind == CloudItemKind.Directory ? sdk.GetDirectory(change.RelativePath) : sdk.GetFile(change.RelativePath);
                SetProtectedFixtureUserOwner(item, owner);
                var snapshot = await item.InspectAsync(token);
                Assert.IsTrue(snapshot.IsPlaceholder);
                Assert.IsNotNull(snapshot.LocalBinding);
                var binding = snapshot.LocalBinding;
                retained.Add((new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), change.RemoteRevision!));
            }
            return retained;
        }
    }
    private sealed class LaterDirectoryMoveSourceReadback(IMirrorPulseWorkerDirectoryPageSource worker,
        string backend, string movedFile, byte[] bytes) : IMirrorPulseWorkerDirectoryPageSource
    {
        private int _edits;
        internal int Edits => Volatile.Read(ref _edits);
        internal string? Revision { get; private set; }

        public async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken)
        {
            string parent = movedFile[..movedFile.LastIndexOf('/')];
            bool change = request.NormalizedPath == parent && Interlocked.CompareExchange(ref _edits, 1, 0) == 0;
            if (change) await File.WriteAllBytesAsync(Path.Combine(backend, movedFile), bytes, cancellationToken);
            var page = await worker.ReadDirectoryPageAsync(request, cancellationToken);
            if (change) Revision = page.Entries.Single(entry => entry.RelativePath == movedFile).RemoteRevision;
            return page;
        }
    }

}
