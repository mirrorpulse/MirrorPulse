using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static readonly string[] CallbackDrainExpectedChildren = ["cold.bin"];
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeLibraryDisposalDrainsPopulationProjectionBeforeNewOwner()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.callback-drain"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var identity = router.CreateFileIdentity(registration.InstanceId, registration.UniquenessKey, "cold", "v1");
        var pause = new PopulationCommitPause(identity.ItemId, timeout.Token);
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await using var execution = new MirrorPulseNamespaceExecutionSession();
            await using var admission = new MirrorPulseNamespaceWorkAdmission([registration.RootId]);
            var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution)
            { DirectoryPage = new([new("cold", "v1", "file", "cold.bin", 4, null, null, false)], default, true) };
            var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
            var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), admission);
            var state = new MirrorPulseCfSharpStateSession(paths);
            await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(new PausingPopulationFactory(state, pause))
                .WithContentProvider(provider).Build();
            await MirrorPulseNamespaceRuntimeStartup.StartAsync(fileSystem, execution, timeout.Token);
            await using var feed = fileSystem.CreateLocalChangeFeed(); await feed.StartAsync(timeout.Token);
            await execution.RunNamespaceOperationAsync(async () =>
                await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
            CloudDirectory docs = fileSystem.GetDirectory("Docs");
            SetProtectedFixtureUserOwner(fileSystem.Root, user.User!); SetProtectedFixtureUserOwner(docs, user.User!);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
            await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await using var anchorGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, timeout.Token);
            var originals = new List<MirrorPulseNamespacePermissionPreparation>();
            foreach (CloudDirectory item in new[] { fileSystem.Root, docs })
            {
                await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, timeout.Token);
                var actual = await lease.InspectAsync(timeout.Token);
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), actual.RootId, actual.LocalObject,
                    actual.RelativePath, true, actual.OwnerSid, actual.Dacl, actual.ObservedAt);
                var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject,
                    baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, execution.RoleSid.Value,
                    baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, true), DateTimeOffset.UtcNow);
                originals.Add(new(baseline, intent));
            }
            await catalog.PrepareNamespacePermissionChangesAsync(originals, timeout.Token);
            await execution.RunNamespaceOperationAsync(async () =>
            {
                for (int index = originals.Count - 1; index >= 0; index--)
                {
                    var result = await permissions.ApplyProtectedAsync(originals[index].Intent.OperationId,
                        index == 0 ? fileSystem.Root : docs, router, cancellationToken: timeout.Token);
                    Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Permission!.Outcome);
                    AssertCompletedRegisteredProtectionScope(result.Receipt!);
                }
            });
            var rootBinding = (await fileSystem.Root.InspectAsync(timeout.Token)).LocalBinding;
            Assert.IsNotNull(rootBinding);
            await using (var opened = await admission.BeginRecoveryAsync([registration.RootId], timeout.Token))
            {
                await foreach (var _ in docs.EnumerateLocalChildrenAsync(cancellationToken: timeout.Token))
                    Assert.Fail("The callback fixture must begin with an empty protected directory.");
                opened.CompleteAfterFreshAudit();
            }
            pause.Armed = true;
            Task<MirrorPulse.CfSharp.CrashProbe.NamespaceSourceConsumerResult> consumer = RunSourceConsumerAsync(directory, "enumerate", execution, timeout.Token);
            try
            {
                await pause.Entered.Task.WaitAsync(timeout.Token);
                Assert.AreEqual(1, worker.Calls.Count(call => call == "directory"));
                string child = Path.Combine(docs.FullPath, "cold.bin");
                Assert.IsTrue(File.Exists(child)); Assert.IsTrue(File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint));
                // FetchChildren has returned and its MP work lease has ended, but the native
                // transfer has not yet reached durable official projection completion.
                await using var drainedApplication = await admission.BeginRecoveryAsync([registration.RootId], timeout.Token);
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                Task disposing = fileSystem.DisposeAsync().AsTask();
                Assert.AreEqual(CloudFileSystemLifecycleState.Stopping, fileSystem.LifecycleState);
                Assert.IsFalse(disposing.IsCompleted); Assert.IsFalse(pause.StoreDisposed.Task.IsCompleted);
                pause.Release.TrySetResult(); await disposing.WaitAsync(timeout.Token);
                Assert.AreEqual(CloudFileSystemLifecycleState.Disposed, fileSystem.LifecycleState);
                Assert.IsTrue(pause.Committed); Assert.IsTrue(pause.StoreDisposed.Task.IsCompleted);
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                var observed = await consumer.WaitAsync(timeout.Token); CollectionAssert.AreEqual(CallbackDrainExpectedChildren, observed.Names);
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => state.OpenStore.BeginTransactionAsync(timeout.Token).AsTask());
                var replacementState = new MirrorPulseCfSharpStateSession(paths);
                await using var replacement = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(replacementState).WithContentProvider(provider).Build();
                await MirrorPulseNamespaceRuntimeStartup.StartAsync(replacement, execution, timeout.Token);
                Assert.AreEqual(rootBinding, (await replacement.Root.InspectAsync(timeout.Token)).LocalBinding);
                await using (var transaction = await replacementState.OpenStore.BeginTransactionAsync(timeout.Token))
                {
                    var persisted = await transaction.Items.GetByItemIdAsync(identity.ItemId, timeout.Token);
                    Assert.IsNotNull(persisted); Assert.AreEqual(identity.RemoteId, persisted.RemoteId); Assert.AreEqual("v1", persisted.RemoteRevision);
                    await transaction.RollbackAsync(timeout.Token);
                }
                var native = await replacement.GetFile("Docs/cold.bin").InspectAsync(timeout.Token);
                Assert.IsTrue(native.IsPlaceholder); Assert.AreEqual(identity, CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span));
                Assert.AreEqual(1, worker.Calls.Count(call => call == "directory"));
                Assert.AreEqual(0, worker.Calls.Count(call => call == "range-open"));
                Assert.IsFalse(admission.IsAdmitted(registration.RootId));
                TestContext.WriteLine("CallbackLifecycleDrain: actualExternalConsumer=True; nativePlaceholderBeforeOfficialCommit=True; publicOfficialSqliteProxyOnly=True; applicationLeaseAlreadyDrained=True; completeLibraryDisposeWaitsForProjection=True; oldStoreDisposedAfterCommit=True; freshFilesystemAndStoreOwner=True; sameRegisteredRootBinding=True; durablePublicIdentityRetained=True; sourceNormalUser=True; sourceRemainsClosed=True; hydration=False; treeReady=False; defaultHostStartup=False.");
            }
            finally
            {
                pause.Release.TrySetResult();
                // The consumer helper owns process cleanup and observes the shared fixture deadline.
                try { await consumer.WaitAsync(timeout.Token); }
                catch (Exception error) when (error is OperationCanceledException or IOException or AssertFailedException)
                { TestContext.WriteLine($"Callback fixture consumer cleanup: {error.GetType().Name}."); }
            }
        }
        finally
        {
            pause.Release.TrySetResult(); registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }

    private sealed class PopulationCommitPause(Guid itemId, CancellationToken deadline)
    {
        private int _selected;
        private int _armed;
        public bool Armed { get => Volatile.Read(ref _armed) != 0; set => Volatile.Write(ref _armed, value ? 1 : 0); }
        public bool Committed { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StoreDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
        {
            if (Armed && Volatile.Read(ref _selected) == 0 && await transaction.Items.GetByItemIdAsync(itemId, token) is not null &&
                Interlocked.CompareExchange(ref _selected, 1, 0) == 0)
            {
                // Deliberately model an already accepted, temporarily non-cooperative storage
                // commit. The mature SQLite transaction still performs all persistence.
                Entered.TrySetResult(); await Release.Task.WaitAsync(deadline);
                await transaction.CommitAsync(CancellationToken.None); Committed = true;
            }
            else await transaction.CommitAsync(token);
        }
    }

    private sealed class PausingPopulationFactory(ICloudStateStoreFactory inner, PopulationCommitPause pause) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new PausingPopulationStore(await inner.OpenAsync(context, cancellationToken), pause);
    }
    private sealed class PausingPopulationStore(ICloudStateStore inner, PopulationCommitPause pause) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new PausingPopulationTransaction(await inner.BeginTransactionAsync(cancellationToken), pause);
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); if (pause.Entered.Task.IsCompleted) pause.StoreDisposed.TrySetResult(); }
    }
    private sealed class PausingPopulationTransaction(ICloudStateTransaction inner, PopulationCommitPause pause) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations => inner.Operations;
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => pause.CommitAsync(inner, cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
