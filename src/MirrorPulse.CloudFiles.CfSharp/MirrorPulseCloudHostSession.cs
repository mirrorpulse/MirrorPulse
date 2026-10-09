using System.Runtime.Versioning;
using System.Text;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public interface IMirrorPulseCloudRuntime : IAsyncDisposable
{
    MirrorPulseJournalPumpHealth? JournalHealth => null;
    IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> RootRenameRecoveryResults => [];
    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseManagedRootRenameRecovery>(
            new NotSupportedException("This Cloud Files runtime does not recover managed root renames."));

    ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
        IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseCloudStatusSnapshot>(
            new NotSupportedException("This Cloud Files runtime does not expose status."));

    ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
        InstanceId instanceId,
        CloudRemoteChangeBatch batch,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center,
        MirrorPulseConflictNotificationBridge notifications,
        CloudRemoteApplyOptions? options,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<CloudRemoteApplyResult>(
            new NotSupportedException("This Cloud Files runtime does not apply remote batches."));

    ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseConflictResolution>(
            new NotSupportedException("This Cloud Files runtime does not resolve upload conflicts."));

    ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        MirrorPulseProductCatalog catalog,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<MirrorPulseRemoteConflictActionOutcome>(
            new NotSupportedException("This Cloud Files runtime does not resolve remote conflicts."));
}

public interface IMirrorPulseCloudRuntimeFactory
{
    IMirrorPulseCloudRuntime Create(MirrorPulseStoragePaths paths);
}

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CfSharpMirrorPulseCloudRuntimeFactory : IMirrorPulseCloudRuntimeFactory
{
    private readonly ICloudDemandProvider? _provider;
    private readonly IMirrorPulseWorkerUploadTransport? _uploads;
    private readonly IMirrorPulseWorkerStatTransport? _stats;
    private readonly IMirrorPulseWorkerMutationTransport? _mutations;
    private readonly MirrorPulseRootRouter? _router;
    private readonly MirrorPulseProductCatalog? _catalog;
    private readonly Func<InstanceId, bool>? _mayDispatch;
    private readonly MirrorPulseConflictCenter? _conflicts;
    private readonly MirrorPulseConflictNotificationBridge? _notifications;
    private readonly MirrorPulseInstanceScheduler? _scheduler;

    public CfSharpMirrorPulseCloudRuntimeFactory(
        ICloudDemandProvider? provider = null,
        IMirrorPulseWorkerUploadTransport? uploads = null,
        IMirrorPulseWorkerStatTransport? stats = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        MirrorPulseRootRouter? router = null,
        MirrorPulseProductCatalog? catalog = null,
        Func<InstanceId, bool>? mayDispatch = null,
        MirrorPulseConflictCenter? conflicts = null,
        MirrorPulseConflictNotificationBridge? notifications = null,
        MirrorPulseInstanceScheduler? scheduler = null)
    {
        _provider = provider;
        _uploads = uploads;
        _stats = stats;
        _mutations = mutations;
        _router = router;
        _catalog = catalog;
        _mayDispatch = mayDispatch;
        _conflicts = conflicts;
        _notifications = notifications;
        _scheduler = scheduler;
    }

    public IMirrorPulseCloudRuntime Create(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var state = new MirrorPulseCfSharpStateSession(paths);
        return new CfSharpRuntime(new MirrorPulseCloudFileSystemBuilder(paths)
            .WithStateStore(state)
            .WithContentProvider(_provider ?? MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath))
            .Build(), state, paths.SyncRootPath, paths.DataRootPath, _uploads, _stats, _mutations, _router, _catalog,
            _mayDispatch, _conflicts, _notifications, _scheduler);
    }

    private sealed class CfSharpRuntime(
        CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state,
        string syncRootPath,
        string dataRootPath,
        IMirrorPulseWorkerUploadTransport? uploads,
        IMirrorPulseWorkerStatTransport? stats,
        IMirrorPulseWorkerMutationTransport? mutations,
        MirrorPulseRootRouter? router,
        MirrorPulseProductCatalog? catalog,
        Func<InstanceId, bool>? mayDispatch,
        MirrorPulseConflictCenter? conflicts,
        MirrorPulseConflictNotificationBridge? notifications,
        MirrorPulseInstanceScheduler? scheduler)
        : IMirrorPulseCloudRuntime
    {
        private MirrorPulseJournalUploadPump? _uploadPump;
        private MirrorPulseUploadConflictActions? _conflictActions;
        private MirrorPulseRemoteConflictActions? _remoteConflictActions;
        private MirrorPulseCloudProviderDiagnostics? _providerDiagnostics;
        private readonly MirrorPulseInstanceScheduler _scheduler = scheduler ?? new();
        private readonly bool _ownsScheduler = scheduler is null;
        private MirrorPulseManagedRootRenameService? _rootRenames;
        public IReadOnlyList<MirrorPulseManagedRootRenameRestoreResult> RootRenameRecoveryResults { get; private set; } = [];
        public MirrorPulseJournalPumpHealth? JournalHealth => _uploadPump?.Health;

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _providerDiagnostics = new(Path.Combine(dataRootPath, "logs", "cloud-files"));
            await fileSystem.StartAsync(cancellationToken).ConfigureAwait(false);
            if (catalog is not null && router is not null)
            {
                var coordinator = new MirrorPulseManagedRootRenameCoordinator(fileSystem, catalog, router);
                _rootRenames = new(catalog, _scheduler, coordinator.RecoverAsync);
                // Recover original namespace evidence before upload dispatch starts.
                // Incomplete and legacy records keep their per-root durable fence.
                RootRenameRecoveryResults = await _rootRenames.RestorePendingAsync(cancellationToken).ConfigureAwait(false);
            }
            if (catalog is not null && conflicts is not null && notifications is not null)
                await new MirrorPulseRemoteConflictProjector(fileSystem, catalog, conflicts, notifications)
                    .RestoreAsync(cancellationToken).ConfigureAwait(false);
            if (uploads is not null && stats is not null && router is not null &&
                catalog is not null && mayDispatch is not null)
            {
                CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                var completion = new MirrorPulseJournalUploadCompletion(
                    feed, state, new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)));
                _uploadPump = new MirrorPulseJournalUploadPump(feed, router, catalog, uploads, stats, state,
                    syncRootPath, dataRootPath, mayDispatch, completion, conflicts, notifications, mutations,
                    uploads as IMirrorPulseWorkerRangeTransport, uploads as IMirrorPulseWorkerDirectoryPageSource, fileSystem, _scheduler);
                _conflictActions = new MirrorPulseUploadConflictActions(catalog, state, feed,
                    new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)),
                    new MirrorPulseStoragePaths(syncRootPath, dataRootPath), router, mutations);
                await _uploadPump.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            if (catalog is not null && conflicts is not null)
            {
                _remoteConflictActions = MirrorPulseRemoteConflictActions.For(fileSystem,
                    new MirrorPulseConflictCopyStore(new MirrorPulseStoragePaths(syncRootPath, dataRootPath)),
                    catalog, conflicts);
            }
        }

        public ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
            Guid operationId, CancellationToken cancellationToken) =>
            (_rootRenames ?? throw new NotSupportedException("The managed root recovery service is unavailable."))
                .RecoverAsync(operationId, cancellationToken);

        public ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            CancellationToken cancellationToken) =>
            (_conflictActions ?? throw new InvalidOperationException(
                "The upload conflict action service has not started.")).ApplyAsync(
                    conflictId, action, cancellationToken);

        public async ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
            Guid conflictId,
            MirrorPulseConflictAction action,
            MirrorPulseProductCatalog catalog,
            CancellationToken cancellationToken)
        {
            MirrorPulseRemoteConflictActions actions = _remoteConflictActions ?? throw new InvalidOperationException(
                "The remote conflict action service has not started.");
            MirrorPulseConflictRecord conflict = (await catalog.ReadRemoteConflictProjectionsAsync(
                cancellationToken).ConfigureAwait(false)).SingleOrDefault(item =>
                    item.ConflictId == conflictId) ?? throw new FileNotFoundException(
                        "The remote conflict was not found.");
            return await actions.ApplyAsync(Guid.NewGuid(), conflict, action,
                action == MirrorPulseConflictAction.KeepBoth
                    ? MirrorPulseConflictPreservedSide.Local
                    : null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
            IEnumerable<InstanceId> instanceIds,
            CancellationToken cancellationToken) =>
            await MirrorPulseCloudStatusReader.ReadAsync(state.OpenStore, instanceIds, cancellationToken)
                .ConfigureAwait(false);

        public ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
            InstanceId instanceId,
            CloudRemoteChangeBatch batch,
            MirrorPulseProductCatalog catalog,
            MirrorPulseConflictCenter center,
            MirrorPulseConflictNotificationBridge notifications,
            CloudRemoteApplyOptions? options,
            CancellationToken cancellationToken)
        {
            var projector = new MirrorPulseRemoteConflictProjector(
                fileSystem, catalog, center, notifications);
            var coordinator = new MirrorPulseRemoteBatchCoordinator(fileSystem, state, projector);
            return coordinator.ApplyAsync(instanceId, batch, options, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    if (_uploadPump is not null)
                        await _uploadPump.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (_ownsScheduler) await _scheduler.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try { await fileSystem.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    if (_providerDiagnostics is not null)
                        await _providerDiagnostics.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}

/// <summary>
/// Owns one current-user Cloud Files process session. Persistent registration and SQLite state
/// survive ordinary shutdown; only explicit account removal unregisters the root.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseCloudHostSession : IAsyncDisposable
{
    private static readonly Guid ProviderId = new("d194eacd-c38c-49df-a238-49871f9c8d1b");
    private static readonly byte[] RootIdentity = Encoding.UTF8.GetBytes("MirrorPulse/cloud-files/v1");

    private readonly MirrorPulseStoragePaths _paths;
    private readonly MirrorPulseSyncRootRegistrationCoordinator _registration;
    private readonly IMirrorPulseCloudRuntimeFactory _runtimeFactory;
    private readonly MirrorPulseSyncRootOwner _owner;
    private readonly MirrorPulseSyncRootDefinition _definition;
    private readonly MirrorPulseShellRegistrationProfile _profile;
    private IMirrorPulseCloudRuntime? _runtime;
    public MirrorPulseJournalPumpHealth? JournalHealth => _runtime?.JournalHealth;
    private bool _started;
    private bool _disposed;

    public MirrorPulseCloudHostSession(
        MirrorPulseStoragePaths paths,
        MirrorPulseSyncRootRegistrationCoordinator registration,
        IMirrorPulseCloudRuntimeFactory runtimeFactory,
        MirrorPulseSyncRootOwner owner,
        string currentUserSid,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(owner);
        _paths = paths;
        _registration = registration;
        _runtimeFactory = runtimeFactory;
        _owner = owner;
        _definition = new MirrorPulseSyncRootDefinition(paths.SyncRootPath, "0.1.0", ProviderId, RootIdentity);
        _profile = MirrorPulseShellSyncRootRegistrar.CreateProfile(_definition, currentUserSid, displayName);
    }

    public MirrorPulseCloudHostSession(
        MirrorPulseStoragePaths paths,
        MirrorPulseSyncRootRegistrationCoordinator registration,
        IMirrorPulseCloudRuntimeFactory runtimeFactory,
        MirrorPulseSyncRootOwner owner,
        string currentUserSid,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations)
        : this(paths, registration, runtimeFactory, owner, currentUserSid,
            MirrorPulseSyncRootDisplayName.Resolve(instances, registrations))
    {
    }

    public MirrorPulseSyncRootDefinition Definition => _definition;

    public MirrorPulseShellRegistrationProfile ShellProfile => _profile;

    public static MirrorPulseCloudHostSession CreateDefault(MirrorPulseStoragePaths paths, string displayName)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(
            paths,
            MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(),
            MirrorPulseSyncRootOwner.CreateDefault(),
            sid,
            displayName);
    }

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations) =>
        CreateDefault(paths, MirrorPulseSyncRootDisplayName.Resolve(instances, registrations));

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations,
        ICloudDemandProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(paths, MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(provider),
            MirrorPulseSyncRootOwner.CreateDefault(), sid, instances, registrations);
    }

    public static MirrorPulseCloudHostSession CreateDefault(
        MirrorPulseStoragePaths paths,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations,
        ICloudDemandProvider provider,
        IMirrorPulseWorkerUploadTransport uploads,
        IMirrorPulseWorkerStatTransport stats,
        MirrorPulseRootRouter router,
        MirrorPulseProductCatalog catalog,
        Func<InstanceId, bool> mayDispatch,
        MirrorPulseConflictCenter? conflicts = null,
        MirrorPulseConflictNotificationBridge? notifications = null,
        IMirrorPulseWorkerMutationTransport? mutations = null,
        MirrorPulseInstanceScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return new(paths, MirrorPulseSyncRootRegistrationCoordinator.CreateDefault(),
            new CfSharpMirrorPulseCloudRuntimeFactory(provider, uploads, stats, mutations, router, catalog,
                mayDispatch, conflicts, notifications, scheduler),
            MirrorPulseSyncRootOwner.CreateDefault(), sid, instances, registrations);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The Cloud Files Host session has already started.");
        }

        if (!_owner.TryAcquire(TimeSpan.Zero))
        {
            throw new InvalidOperationException("Another MirrorPulse Host owns this user's Cloud Files root.");
        }

        try
        {
            _owner.EnsureHeld();
            await _registration.EnsureRegisteredAsync(_definition, _profile, cancellationToken).ConfigureAwait(false);
            _runtime = _runtimeFactory.Create(_paths);
            await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        catch (Exception startFailure)
        {
            try
            {
                if (_runtime is not null)
                {
                    await _runtime.DisposeAsync().ConfigureAwait(false);
                }

                _runtime = null;
                _owner.Dispose();
                _disposed = true;
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Cloud Files startup and resource cleanup both failed.",
                    startFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public ValueTask<MirrorPulseCloudStatusSnapshot> ReadStatusAsync(
        IEnumerable<InstanceId> instanceIds,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ReadStatusAsync(instanceIds, cancellationToken);
    }

    public ValueTask<MirrorPulseManagedRootRenameRecovery> RecoverManagedRootRenameAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        return _runtime.RecoverManagedRootRenameAsync(operationId, cancellationToken);
    }

    public ValueTask<CloudRemoteApplyResult> ApplyRemoteBatchAsync(
        InstanceId instanceId,
        CloudRemoteChangeBatch batch,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center,
        MirrorPulseConflictNotificationBridge notifications,
        CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyRemoteBatchAsync(instanceId, batch, catalog, center, notifications,
            options, cancellationToken);
    }

    public ValueTask<MirrorPulseConflictResolution> ApplyUploadConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyUploadConflictAsync(conflictId, action, cancellationToken);
    }

    public ValueTask<MirrorPulseRemoteConflictActionOutcome> ApplyRemoteConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        MirrorPulseProductCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _runtime is null)
        {
            throw new InvalidOperationException("The Cloud Files Host session has not started.");
        }

        return _runtime.ApplyRemoteConflictAsync(conflictId, action, catalog, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _runtime = null;
        }

        _owner.Dispose();
        _disposed = true;
    }
}
