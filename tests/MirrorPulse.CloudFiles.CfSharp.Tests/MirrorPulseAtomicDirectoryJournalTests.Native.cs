using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseAtomicDirectoryJournalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles"), TestCategory("SignedAtomicDirectory")]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task NativeAtomicDirectoryPumpRecoversAroundPublicFeedAcknowledgementAndRetainsTheLaterDelete(
        bool failOfficialCommit, bool failHostReceiptCommit)
    {
        string? package = Environment.GetEnvironmentVariable("MP_SIGNED_LOCAL_PACKAGE");
        string? executable = Environment.GetEnvironmentVariable("MP_S06_ATOMIC_LOCAL_WORKER_EXE");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" || (package is null && executable is null))
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        string cache = Path.Combine(fixture.Paths.DataRootPath, "transfer-cache");
        string owned = Path.Combine(cache, "temporary", "mp-local-atomic-" + Guid.NewGuid().ToString("N"));
        string backend = Path.Combine(owned, "configured-storage");
        string backendTree = Path.Combine(backend, "tree");
        Directory.CreateDirectory(backendTree);
        await File.WriteAllTextAsync(Path.Combine(backendTree, "leaf.txt"), "original bytes", token);
        await File.WriteAllTextAsync(Path.Combine(backend, "peer.txt"), "peer bytes", token);
        var topology = await PrepareTopologyAsync();
        var registration = topology.Roots.Single();
        var instance = registration.InstanceId;
        string rootKey = registration.UniquenessKey;
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, topology.Roots);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        bool registered = false;
        try
        {
            registry.Register(new(fixture.Paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            registered = true;
            Guid admissionId;
            Guid[] originals;
            Guid? laterId = null;
            byte[]? nativeReceipt = null;
            string workerState = Path.Combine(fixture.Paths.DataRootPath, "state", "workers", instance.Value.ToString("N"));
            var state = new MirrorPulseCfSharpStateSession(fixture.Paths);
            var sdkFault = new MirrorPulseJournalAcknowledgementCommitFault();
            await using (var sdk = new MirrorPulseCloudFileSystemBuilder(fixture.Paths)
                .WithStateStore(failOfficialCommit ? sdkFault.Wrap(state) : state)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build())
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
            await using (var worker = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore()))
            await using (var work = new MirrorPulseNamespaceWorkAdmission([registration.RootId]))
            await using (var execution = new MirrorPulseNamespaceExecutionSession())
            {
                var transport = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                await sdk.StartAsync(token);
                (await sdk.Root.CreatePlaceholdersAsync(router.CreateRootPage().Children, cancellationToken: token)).ThrowIfAnyFailed();
                await catalog.SaveAdapterTopologyAsync(topology, token);
                await worker.StartAsync(topology);
                while (!worker.IsConnected(instance)) await Task.Delay(25, token);
                Assert.IsTrue(worker.SupportsAtomicDirectoryDeletion(instance));
                await ApplyOriginalPlaceholdersAsync(sdk, worker);
                await using var feed = sdk.CreateLocalChangeFeed();
                await feed.StartAsync(token);
                await sdk.GetFile("Docs/tree/leaf.txt").DeleteAsync(token);
                var leaf = await ReadDeleteAsync(feed, "Docs/tree/leaf.txt");
                await sdk.GetDirectory("Docs/tree").DeleteAsync(token);
                var tree = await ReadDeleteAsync(feed, "Docs/tree");
                admissionId = tree.OperationId;
                originals = [leaf.OperationId, tree.OperationId];
                if (failOfficialCommit) sdkFault.Arm(tree.OperationId);
                string nativeReceiptPath = Path.Combine(workerState, admissionId.ToString("N") + ".committed.json");
                if (failHostReceiptCommit)
                {
                    await fixture.ProductSqlAsync("""
                        CREATE TRIGGER interrupt_native_receipt BEFORE UPDATE OF receipt ON original_atomic_directory_batches
                        BEGIN SELECT RAISE(ABORT,'interrupted native receipt'); END;
                        """);
                }
                await fixture.ProductSqlAsync($"""
                        CREATE TRIGGER interrupt_native_atomic_ack BEFORE UPDATE ON mutation_intents
                        WHEN NEW.operation_id='{tree.OperationId:D}' AND NEW.state={(int)MirrorPulseMutationState.Acknowledged}
                        BEGIN SELECT RAISE(ABORT,'interrupted native atomic ACK'); END;
                        """);
                await using (var audit = await work.BeginRecoveryAsync([registration.RootId], token))
                {
                    var snapshot = await sdk.GetDirectory("Docs").InspectAsync(token);
                    Assert.IsTrue(snapshot.Exists);
                    Assert.IsNotNull(snapshot.LocalBinding);
                    audit.CompleteAfterFreshAudit();
                }
                var completion = new MirrorPulseJournalUploadCompletion(feed, state, new BackoffPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
                await using (var pump = new MirrorPulseJournalUploadPump(feed, router, catalog, transport, transport, state,
                    fixture.Paths.SyncRootPath, fixture.Paths.DataRootPath, _ => true, completion, mutations: transport,
                    ranges: transport, directories: transport, fileSystem: sdk, namespaceWork: work))
                {
                    await pump.StartAsync(token);
                    if (failHostReceiptCommit)
                    {
                        while (!File.Exists(nativeReceiptPath)) await Task.Delay(25, token);
                        nativeReceipt = await File.ReadAllBytesAsync(nativeReceiptPath, token);
                        Assert.IsNull((await catalog.ReadAtomicDirectoryBatchAsync(admissionId, token))!.Receipt);
                        foreach (Guid original in originals) Assert.IsTrue(await IsPendingAsync(state.OpenStore, original));
                    }
                    else if (failOfficialCommit)
                    {
                        while (sdkFault.Failures == 0) await Task.Delay(10, token);
                        Assert.IsTrue(await IsPendingAsync(state.OpenStore, tree.OperationId));
                        Assert.IsTrue(await IsPendingAsync(state.OpenStore, leaf.OperationId));
                    }
                    else
                    {
                        while (await IsPendingAsync(state.OpenStore, tree.OperationId)) await Task.Delay(25, token);
                        Assert.IsFalse(await IsPendingAsync(state.OpenStore, leaf.OperationId));
                    }
                    Assert.IsFalse(Directory.Exists(backendTree));
                    if (!failHostReceiptCommit)
                    {
                        var retained = await catalog.ReadAcceptedAtomicDirectoryJournalAsync(admissionId, token);
                        Assert.IsNotNull(retained.ReceiptFingerprint);
                        Assert.IsFalse(retained.Completed);
                    }
                }
                CollectionAssert.AreEquivalent(originals,
                    (await catalog.ReadUnsettledAtomicDirectoryOperationIdsAsync(token)).ToArray());
                var rescan = new MirrorPulseFullRescanPolicy(sdk, feed, state, router, catalog, transport, transport,
                    _ => true, transport, transport, transport, namespaceWork: work);
                await rescan.ReconcileAsync(token);
                Assert.IsFalse((await catalog.ReadBlockedLocalOperationsAsync(token))
                    .Any(operation => operation.RelativePath == "Docs/tree"));
                CollectionAssert.AreEquivalent(originals,
                    (await catalog.ReadUnsettledAtomicDirectoryOperationIdsAsync(token)).ToArray());
                if (failHostReceiptCommit) await catalog.BeginFullRescanAsync(token);
                else
                {
                    await sdk.GetFile("Docs/peer.txt").DeleteAsync(token);
                    laterId = (await ReadDeleteAsync(feed, "Docs/peer.txt")).OperationId;
                    Assert.IsTrue(await IsPendingAsync(state.OpenStore, laterId.Value));
                }
            }
            await fixture.ProductSqlAsync("DROP TRIGGER interrupt_native_atomic_ack;");
            if (failHostReceiptCommit) await fixture.ProductSqlAsync("DROP TRIGGER interrupt_native_receipt;");
            Directory.CreateDirectory(backendTree);
            await File.WriteAllTextAsync(Path.Combine(backendTree, "leaf.txt"), "successor bytes", token);
            var reopenedState = new MirrorPulseCfSharpStateSession(fixture.Paths);
            await using var reopenedSdk = new MirrorPulseCloudFileSystemBuilder(fixture.Paths).WithStateStore(reopenedState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(fixture.Paths.SyncRootPath)).Build();
            await reopenedSdk.StartAsync(token);
            await using var reopenedFeed = reopenedSdk.CreateLocalChangeFeed();
            await reopenedFeed.StartAsync(token);
            await using var reopenedCatalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
            await using var restartedWorker = new AdapterInstanceProcessSupervisor(reopenedCatalog, new WindowsCredentialManagerStore());
            await using var restartedWork = new MirrorPulseNamespaceWorkAdmission([registration.RootId]);
            await using var restartedExecution = new MirrorPulseNamespaceExecutionSession();
            var restartedTransport = new MirrorPulseNormalUserWorkerTransport(restartedExecution,
                restartedWorker, restartedWorker, restartedWorker, restartedWorker, restartedWorker);
            if (failHostReceiptCommit)
            {
                Assert.IsNotNull(await reopenedCatalog.ReadFullRescanAsync(token));
                await restartedWorker.StartAsync(topology);
                while (!restartedWorker.IsConnected(instance)) await Task.Delay(25, token);
                await using var audit = await restartedWork.BeginRecoveryAsync([registration.RootId], token);
                audit.CompleteAfterFreshAudit();
            }
            var reopenedCompletion = new MirrorPulseJournalUploadCompletion(reopenedFeed, reopenedState,
                new BackoffPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
            await using var recovery = new MirrorPulseJournalUploadPump(reopenedFeed, router, reopenedCatalog, restartedTransport, restartedTransport,
                reopenedState, fixture.Paths.SyncRootPath, fixture.Paths.DataRootPath, _ => failHostReceiptCommit, reopenedCompletion,
                mutations: restartedTransport, ranges: restartedTransport, directories: restartedTransport, fileSystem: reopenedSdk, namespaceWork: restartedWork);
            await recovery.StartAsync(token);
            while (!(await reopenedCatalog.ReadAtomicDirectoryJournalAsync(admissionId, token)).Completed)
                await Task.Delay(25, token);
            foreach (Guid id in originals)
            {
                Assert.IsFalse(await IsPendingAsync(reopenedState.OpenStore, id));
                Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await reopenedCatalog.ReadMutationAsync(id, token))!.State);
            }
            Assert.IsEmpty(await reopenedCatalog.ReadUnsettledAtomicDirectoryOperationIdsAsync(token));
            if (laterId is { } independent) Assert.IsTrue(await IsPendingAsync(reopenedState.OpenStore, independent));
            if (nativeReceipt is not null)
            {
                while (await reopenedCatalog.ReadFullRescanAsync(token) is not null) await Task.Delay(25, token);
                var accepted = (await reopenedCatalog.ReadAtomicDirectoryBatchAsync(admissionId, token))!.Receipt!;
                var evidence = MirrorPulse.Adapter.Sdk.AdapterProtocolJson.Decode<System.Text.Json.JsonElement>(Convert.FromBase64String(accepted.BackendEvidence));
                Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nativeReceipt)),
                    evidence.GetProperty("nativeReceiptSha256").GetString());
            }
            Assert.AreEqual("successor bytes", await File.ReadAllTextAsync(Path.Combine(backendTree, "leaf.txt"), token));
            Assert.AreEqual("peer bytes", await File.ReadAllTextAsync(Path.Combine(backend, "peer.txt"), token));
            if (package is not null)
            {
                await using var packageStream = File.OpenRead(package);
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, token));
                TestContext.WriteLine($"SignedAtomicDirectory: packageSha256={digest}; originalCount={originals.Length}; failOfficialCommit={failOfficialCommit}; failHostReceiptCommit={failHostReceiptCommit}.");
            }
        }
        finally { if (registered) registry.Unregister(fixture.Paths.SyncRootPath); }

        async Task ApplyOriginalPlaceholdersAsync(CloudFileSystem sdk, AdapterInstanceProcessSupervisor worker)
        {
            var changes = new List<CloudRemoteChange>();
            foreach (string relative in new[] { "tree", "tree/leaf.txt", "peer.txt" })
            {
                bool directory = relative == "tree";
                string revision = (await worker.StatAsync(new(instance, relative, rootKey), token))!;
                var identity = router.CreateFileIdentity(instance, rootKey, relative, revision);
                var metadata = directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build();
                changes.Add(new("seed-" + relative, directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                    identity.RemoteId, revision, directory ? CloudItemKind.Directory : CloudItemKind.File, "Docs/" + relative, identity.ItemId,
                    length: directory ? null : new FileInfo(Path.Combine(backend, relative)).Length,
                    metadata: metadata, cursorAfter: new byte[] { (byte)(changes.Count + 1) }));
            }
            var applied = await sdk.ApplyRemoteChangesAsync(new CloudRemoteChangeBatch(instance + "/atomic-originals", ReadOnlyMemory<byte>.Empty,
                changes, new byte[] { 3 }), new CloudRemoteApplyOptions { MaximumEntries = 3 }, token);
            Assert.AreEqual(CloudRemoteBatchStatus.Applied, applied.Status);
            Assert.IsEmpty(applied.ConflictIds);
        }

        async Task<MirrorPulseAdapterTopology> PrepareTopologyAsync()
        {
            string files = Path.Combine(fixture.Paths.DataRootPath, "files");
            if (package is not null)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
                string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                var installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                    Path.Combine(fixture.Paths.DataRootPath, "installed"), runtime, cancellationToken: token);
                Assert.IsTrue(installed.IsSigned);
                Assert.AreEqual("com.mirrorpulse.adapter.local", installed.AdapterId.Value);
                string key = installed.Manifest.RootDefinitions.Single().Key;
                await catalog.CreateInstanceAsync(installed.InstallId, "Docs",
                    new Dictionary<string, string> { ["sourceDirectory"] = backend }, [], files, cache,
                    rootLabels: new Dictionary<string, string> { [key] = "Docs" }, cancellationToken: token);
                return await catalog.ReadAdapterTopologyAsync(token);
            }

            Assert.IsTrue(File.Exists(executable));
            var id = InstanceId.New();
            var adapter = new AdapterId("example.atomic-local");
            var installationId = InstallId.New();
            var root = new RootRegistration(adapter, id, RootId.New(), "docs", "Docs", "Docs", false,
                RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
            var manifest = new AdapterManifest(1, adapter, "Atomic Local fixture", "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
            var installation = new InstalledAdapter(manifest, installationId, Path.GetDirectoryName(executable)!,
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            var configuration = new AdapterInstance(adapter, installationId, id, "Atomic Local",
                new Dictionary<string, string> { ["root.docs.sourceDirectory"] = backend }, [], files,
                cache, true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            return new([installation], [configuration], [root]);
        }

        async Task<CloudLocalChange> ReadDeleteAsync(CloudLocalChangeFeed feed, string path)
        {
            while (true)
            {
                var scan = await feed.BeginScanAsync(token);
                Assert.IsFalse(scan.RequiresFullRescan);
                var page = await feed.ReadPageAsync(scan, 0, 128, token);
                var original = page.Changes.SingleOrDefault(change => change.Kind == CloudLocalChangeKind.Delete &&
                    change.RelativePath.Replace('\\', '/') == path);
                if (original is not null) return original;
                await Task.Delay(25, token);
            }
        }

        async Task<bool> IsPendingAsync(ICloudStateStore store, Guid id)
        {
            await using var tx = await store.BeginTransactionAsync(token);
            bool pending = await tx.Operations.GetAsync(id, token) is not null;
            await tx.RollbackAsync(token);
            return pending;
        }
    }
}
