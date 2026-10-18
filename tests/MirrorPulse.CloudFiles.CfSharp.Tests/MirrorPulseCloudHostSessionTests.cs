using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
public sealed class MirrorPulseCloudHostSessionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RuntimeCleanupFailureReleasesThePersistentRootOwnerAndPreservesStartupFailure(bool failStart)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var owner = new RecordingOwnerLock(); var cleanupFailure = new IOException("Synthetic runtime disposal failure.");
        var factory = new RecordingRuntimeFactory { FailStart = failStart, Cleanup = () => ValueTask.FromException(cleanupFailure) };
        var shell = new RecordingShellRegistry(); var cloud = new RecordingCloudRegistry();
        var session = new MirrorPulseCloudHostSession(paths, new(shell, cloud), factory, new(owner), "S-1-5-21-123", "MirrorPulse");
        try
        {
            if (failStart)
            {
                var actual = await Assert.ThrowsExactlyAsync<AggregateException>(() => session.StartAsync().AsTask());
                Assert.HasCount(2, actual.InnerExceptions); Assert.IsInstanceOfType<InvalidOperationException>(actual.InnerExceptions[0]);
                Assert.AreSame(cleanupFailure, actual.InnerExceptions[1]);
            }
            else
            {
                await session.StartAsync(); Assert.IsTrue(owner.IsHeld);
                Assert.AreSame(cleanupFailure, await Assert.ThrowsExactlyAsync<IOException>(() => session.DisposeAsync().AsTask()));
            }
            Assert.IsFalse(owner.IsHeld, "A failed runtime cleanup must still release the persistent sync-root owner.");
            Assert.AreEqual(1, factory.Runtimes.Single().DisposeCount); Assert.AreEqual(0, shell.UnregisterCount); Assert.AreEqual(0, cloud.UnregisterCount);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.StartAsync().AsTask());
        }
        finally
        {
            try { await session.DisposeAsync(); } catch (IOException) { }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentDisposalAwaitsOneRuntimeDrainBeforeReleasingThePersistentOwner()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var owner = new RecordingOwnerLock(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingRuntimeFactory { Cleanup = async () => await release.Task };
        var session = new MirrorPulseCloudHostSession(paths, new(new RecordingShellRegistry(), new RecordingCloudRegistry()),
            factory, new(owner), "S-1-5-21-123", "MirrorPulse");
        try
        {
            await session.StartAsync(); Task first = session.DisposeAsync().AsTask(), second = session.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted); Assert.IsFalse(second.IsCompleted); Assert.IsTrue(owner.IsHeld);
            try { Assert.AreEqual(1, factory.Runtimes.Single().DisposeCount, "Concurrent callers must share the same actual runtime teardown."); }
            finally { release.TrySetResult(); }
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsFalse(owner.IsHeld);
            await session.DisposeAsync(); Assert.AreEqual(1, factory.Runtimes.Single().DisposeCount);
        }
        finally
        {
            release.TrySetResult(); await session.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows10.0.26100")]
    public async Task ImportCommandsRequireStartedOwnerAndForwardTheOriginalOperation()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var factory = new RecordingRuntimeFactory();
        await using var session = new MirrorPulseCloudHostSession(paths,
            new(new RecordingShellRegistry(), new RecordingCloudRegistry()), factory,
            new(new RecordingOwnerLock()), "S-1-5-21-123", "MirrorPulse");
        Guid operation = Guid.NewGuid(), parent = Guid.NewGuid();
        string source = Path.Combine(root, "input.bin");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.PrepareNamespaceImportAsync(operation, parent, "import.bin", source).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ExecuteNamespaceImportAsync(operation).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReadNamespaceImportStatusAsync(operation).AsTask());
        await session.StartAsync();
        var plan = await session.PrepareNamespaceImportAsync(operation, parent, "import.bin", source);
        var runtime = factory.Runtimes.Single();
        Assert.AreEqual(operation, plan.OperationId); Assert.AreEqual(source, runtime.ImportSource);
        Assert.AreEqual(parent, runtime.ImportParent); Assert.AreEqual("import.bin", runtime.ImportName);
        Assert.AreEqual(operation, (await session.ExecuteNamespaceImportAsync(operation)).MemberId);
        Assert.IsTrue((await session.ReadNamespaceImportStatusAsync(operation)).Registered);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => session.ReadNamespaceImportStatusAsync(operation, cancelled.Token).AsTask());
    }

    [TestMethod]
    public async Task RootRecoveryRequiresStartedOwnerAndForwardsOriginalOperation()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var factory = new RecordingRuntimeFactory();
        await using var session = new MirrorPulseCloudHostSession(paths,
            new(new RecordingShellRegistry(), new RecordingCloudRegistry()), factory,
            new(new RecordingOwnerLock()), "S-1-5-21-123", "MirrorPulse");
        Guid operation = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.RecoverManagedRootRenameAsync(operation).AsTask());
        await session.StartAsync();
        var receipt = await session.RecoverManagedRootRenameAsync(operation);
        Assert.AreEqual(operation, receipt.Intent.OperationId);
        Assert.AreEqual(operation, factory.Runtimes.Single().RecoveredOperation);
        Assert.IsNull(receipt.LibraryResult, "The recording runtime does not claim native reconciliation.");
    }

    [TestMethod]
    public async Task CustomInstanceNameSwitchesBackToUnifiedShellNameWhenAnotherInstanceAppears()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var first = CreateInstance("Personal drive");
        var second = CreateInstance("Backup");
        AdapterId adapterId = first.AdapterId;
        RootRegistration custom = AdapterRootRegistrationMapper.Map(adapterId, first.InstanceId,
            new AdapterRootDefinition("files", "Files", "Files", true), RootRegistrationState.Active);
        RootRegistration backup = AdapterRootRegistrationMapper.Map(adapterId, second.InstanceId,
            new AdapterRootDefinition("backup", "Backup", "Backup", false), RootRegistrationState.Active);
        var shell = new RecordingShellRegistry();
        var coordinator = new MirrorPulseSyncRootRegistrationCoordinator(shell, new RecordingCloudRegistry());

        try
        {
            foreach ((AdapterInstance[] instances, RootRegistration[] registrations) in new[]
            {
                (new[] { first }, new[] { custom }),
                (new[] { first, second }, new[] { custom, backup }),
            })
            {
                await using var session = new MirrorPulseCloudHostSession(paths, coordinator,
                    new RecordingRuntimeFactory(), new MirrorPulseSyncRootOwner(new RecordingOwnerLock()),
                    "S-1-5-21-123", instances, registrations);
                await session.StartAsync();
            }

            Assert.HasCount(2, shell.Profiles);
            Assert.AreEqual("Personal drive", shell.Profiles[0].DisplayName);
            Assert.AreEqual("MirrorPulse", shell.Profiles[1].DisplayName);
            Assert.AreEqual(shell.Profiles[0].RegistrationId, shell.Profiles[1].RegistrationId);
            Assert.AreEqual(paths.SyncRootPath, shell.Profiles[1].SyncRootPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AdapterInstance CreateInstance(string displayName)
    {
        var instance = InstanceId.New();
        return new AdapterInstance(AdapterId.Parse("example.drive"), InstallId.New(), instance,
            displayName, new Dictionary<string, string>(), [],
            Path.Combine(Path.GetTempPath(), instance.ToString(), "files"),
            Path.Combine(Path.GetTempPath(), instance.ToString(), "transfer"),
            true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("PackagedShell")]
    public async Task PackagedSessionReopensOfficialSqliteDatabase()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_TEST") != "1")
        {
            Assert.Inconclusive("Requires the PackagedShell test environment; run the dedicated verification gate.");
        }

        Assert.AreEqual("0B72358D-6DC9-479D-8C28-F0232B42A0B3",
            Windows.ApplicationModel.Package.Current.Id.Name);

        var root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseSyncRootDefinition? definition = null;
        MirrorPulseShellRegistrationProfile? profile = null;
        bool registered = false;
        try
        {
            for (int run = 0; run < 2; run++)
            {
                await using var session = MirrorPulseCloudHostSession.CreateDefault(paths, "MirrorPulse");
                definition = session.Definition;
                profile = session.ShellProfile;
                await session.StartAsync();
                registered = true;
                Assert.AreEqual("MirrorPulse", CloudSyncRoot.Open(paths.SyncRootPath).GetInfo().ProviderName);
                Assert.IsTrue(File.Exists(paths.CfSharpStateDatabasePath));
                MirrorPulseCloudStatusSnapshot status = await session.ReadStatusAsync([]);
                Assert.AreEqual(0, status.PendingUploadCount);
            }
        }
        finally
        {
            if (registered && definition is not null && profile is not null)
            {
                MirrorPulseSyncRootRegistrationCoordinator.CreateDefault()
                    .UnregisterForRemoval(definition, profile);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RestartReusesRootIdentityAndLeavesRegistrationInstalled()
    {
        var root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var shell = new RecordingShellRegistry();
        var cloud = new RecordingCloudRegistry();
        var factory = new RecordingRuntimeFactory();
        var registration = new MirrorPulseSyncRootRegistrationCoordinator(shell, cloud);
        try
        {
            for (int run = 0; run < 2; run++)
            {
                var ownerLock = new RecordingOwnerLock();
                await using var session = new MirrorPulseCloudHostSession(
                    paths, registration, factory, new MirrorPulseSyncRootOwner(ownerLock),
                    "S-1-5-21-123", "MirrorPulse");
                await session.StartAsync();
                Assert.IsTrue(ownerLock.IsHeld);
                Assert.AreEqual(1, factory.Runtimes[^1].StartCount);
                Assert.AreEqual("MirrorPulse!S-1-5-21-123!Default", session.ShellProfile.RegistrationId);
                Assert.AreEqual(paths.SyncRootPath, session.Definition.Path);
            }

            Assert.HasCount(2, cloud.Registered);
            Assert.AreEqual(cloud.Registered[0].ProviderId, cloud.Registered[1].ProviderId);
            CollectionAssert.AreEqual(cloud.Registered[0].Identity, cloud.Registered[1].Identity);
            Assert.AreEqual(0, cloud.UnregisterCount);
            Assert.AreEqual(0, shell.UnregisterCount);
            Assert.IsTrue(factory.Runtimes.All(runtime => runtime.DisposeCount == 1));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task FailedRuntimeStartReleasesOwnerWithoutRemovingRegistration()
    {
        var root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var shell = new RecordingShellRegistry();
        var cloud = new RecordingCloudRegistry();
        var factory = new RecordingRuntimeFactory { FailStart = true };
        var ownerLock = new RecordingOwnerLock();
        try
        {
            await using var session = new MirrorPulseCloudHostSession(
                paths,
                new MirrorPulseSyncRootRegistrationCoordinator(shell, cloud),
                factory,
                new MirrorPulseSyncRootOwner(ownerLock),
                "S-1-5-21-123",
                "MirrorPulse");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.StartAsync().AsTask());
            Assert.IsFalse(ownerLock.IsHeld);
            Assert.AreEqual(1, factory.Runtimes[0].DisposeCount);
            Assert.AreEqual(0, cloud.UnregisterCount);
            Assert.AreEqual(0, shell.UnregisterCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingShellRegistry : IMirrorPulseShellRootRegistry
    {
        public List<MirrorPulseShellRegistrationProfile> Profiles { get; } = [];

        public int UnregisterCount { get; private set; }

        public ValueTask<bool> RegisterAsync(MirrorPulseShellRegistrationProfile profile, CancellationToken cancellationToken)
        {
            Profiles.Add(profile);
            return ValueTask.FromResult(false);
        }

        public void Unregister(MirrorPulseShellRegistrationProfile profile) => UnregisterCount++;
    }

    private sealed class RecordingCloudRegistry : IMirrorPulseCloudRootRegistry
    {
        public List<MirrorPulseSyncRootDefinition> Registered { get; } = [];

        public int UnregisterCount { get; private set; }

        public void EnsureCompatible(MirrorPulseSyncRootDefinition definition)
        {
        }

        public void Register(MirrorPulseSyncRootDefinition definition) => Registered.Add(definition);

        public void Unregister(string syncRootPath) => UnregisterCount++;
    }

    private sealed class RecordingRuntimeFactory : IMirrorPulseCloudRuntimeFactory
    {
        public bool FailStart { get; init; }
        public Func<ValueTask>? Cleanup { get; init; }

        public List<RecordingRuntime> Runtimes { get; } = [];

        public IMirrorPulseCloudRuntime Create(MirrorPulseStoragePaths paths)
        {
            var runtime = new RecordingRuntime(FailStart, Cleanup);
            Runtimes.Add(runtime);
            return runtime;
        }
    }

    private sealed class RecordingRuntime(bool failStart, Func<ValueTask>? cleanup = null) : IMirrorPulseCloudRuntime
    {
        public string? ImportSource { get; private set; }
        public string? ImportName { get; private set; }
        public Guid ImportParent { get; private set; }
        private Guid _importOperation;

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulse.Core.State.MirrorPulseNamespaceBirthPlan> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
            string name, string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _importOperation = operationId; ImportParent = parentMemberId; ImportName = name; ImportSource = sourcePath;
            return ValueTask.FromResult(new MirrorPulse.Core.State.MirrorPulseNamespaceBirthPlan(2, operationId, Guid.NewGuid(), "local:test", null, DateTimeOffset.UtcNow)
            { Content = new(0, new string('A', 64)) });
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulse.Core.State.MirrorPulseNamespaceMemberRegistration> ExecuteNamespaceImportAsync(Guid operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(_importOperation, operationId);
            return ValueTask.FromResult(new MirrorPulse.Core.State.MirrorPulseNamespaceMemberRegistration(1, operationId, ImportParent,
                MirrorPulse.Core.State.MirrorPulseNamespaceMemberOrigin.ProtectedBirth, operationId, Guid.NewGuid(), RootId.New(), "Docs/import.bin",
                new(1, Guid.NewGuid(), Guid.NewGuid()), false, "S-1-5-21-123", DateTimeOffset.UtcNow));
        }

        [SupportedOSPlatform("windows10.0.26100")]
        public ValueTask<MirrorPulseNamespaceImportStatus> ReadNamespaceImportStatusAsync(Guid operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(_importOperation, operationId);
            return ValueTask.FromResult(new MirrorPulseNamespaceImportStatus(operationId, "Docs/import.bin", "Registered", 0, new string('A', 64),
                true, true, true, true, true, true, false));
        }

        public Guid? RecoveredOperation { get; private set; }

        public ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
            Guid operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveredOperation = operationId;
            return ValueTask.FromResult(new MirrorPulseManagedRootRenameRecovery(
                new(operationId, RootId.New(), "Docs", "Renamed", MirrorPulse.Core.State.MirrorPulseRootRenamePhase.Prepared,
                    DateTimeOffset.UtcNow), null));
        }

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            if (failStart)
            {
                throw new InvalidOperationException("The native session could not start.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return cleanup?.Invoke() ?? ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOwnerLock : ICurrentUserOwnerLock
    {
        public bool IsHeld { get; private set; }

        public bool TryAcquire(TimeSpan timeout)
        {
            IsHeld = true;
            return true;
        }

        public void Release() => IsHeld = false;
    }
}
