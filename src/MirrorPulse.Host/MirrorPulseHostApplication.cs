using System.Text.Json;
using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Control.Compatibility;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Host;

/// <summary>
/// Owns the complete non-UI Host composition for one current user.
/// </summary>
public sealed class MirrorPulseHostApplication : IAsyncDisposable
{
    private readonly MirrorPulseStoragePaths _paths;
    private readonly MirrorPulseConfigurationStore _configurationStore;
    private MirrorPulseConfiguration _configuration;
    private readonly MirrorPulseHostLease _hostLease;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseConflictCenter _conflictCenter;
    private readonly MirrorPulseSystemNotificationPublisher _systemNotifications;
    private readonly AdapterInstanceProcessSupervisor _workers;
    private readonly MirrorPulseCloudHostSession _session;
    private readonly MirrorPulseActiveRemotePoller _remotePoller;
    private readonly MirrorPulseInstanceScheduler _remoteScheduler;
    private readonly MirrorPulseAdapterTopology _topology;
    private readonly CancellationTokenSource _shutdown = new();
    private MirrorPulseAppStatusPipe? _statusPipe;
    private MirrorPulseControlPipeServer? _controlPipe;
    private Task? _serveTask;
    private MirrorPulseLifecycleState _state = MirrorPulseLifecycleState.Created;
    private DateTimeOffset _startedAt;
    private string? _requestedAction;
    private bool _disposed;

    private MirrorPulseHostApplication(
        MirrorPulseStoragePaths paths,
        MirrorPulseConfigurationStore configurationStore,
        MirrorPulseConfiguration configuration,
        MirrorPulseHostLease hostLease,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter conflictCenter,
        MirrorPulseSystemNotificationPublisher systemNotifications,
        AdapterInstanceProcessSupervisor workers,
        MirrorPulseCloudHostSession session,
        MirrorPulseActiveRemotePoller remotePoller,
        MirrorPulseInstanceScheduler remoteScheduler,
        MirrorPulseAdapterTopology topology)
    {
        _paths = paths;
        _configurationStore = configurationStore;
        _configuration = configuration;
        _hostLease = hostLease;
        _catalog = catalog;
        _conflictCenter = conflictCenter;
        _systemNotifications = systemNotifications;
        _workers = workers;
        _session = session;
        _remotePoller = remotePoller;
        _remoteScheduler = remoteScheduler;
        _topology = topology;
    }

    public MirrorPulseStoragePaths Paths => _paths;

    public MirrorPulseLifecycleState State => _state;

    public bool IsRunning => _state is MirrorPulseLifecycleState.Running or MirrorPulseLifecycleState.Degraded;

    public MirrorPulseHostStatus GetHostStatus() => new(
        (_state == MirrorPulseLifecycleState.Running && _session.JournalHealth is { Healthy: false }
            ? MirrorPulseLifecycleState.Degraded : _state).ToString(),
        Environment.ProcessId,
        MirrorPulseControlPipeNames.CurrentUserV1(),
        _startedAt,
        IsRunning,
        IsRunning,
        _requestedAction);

    public static async Task<MirrorPulseHostApplication> CreateAsync(
        MirrorPulseStoragePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var hostLease = MirrorPulseHostLease.CreateDefault();
        // A prior Host may have closed its control pipe while still releasing the
        // Cloud Files session. Give a CLI-initiated restart time to finish teardown.
        if (!hostLease.TryAcquire(TimeSpan.FromSeconds(5)))
        {
            hostLease.Dispose();
            throw new InvalidOperationException("Another MirrorPulse Host owns this current user's Host lease.");
        }

        MirrorPulseProductCatalog? catalog = null;
        MirrorPulseSystemNotificationPublisher? systemNotifications = null;
        AdapterInstanceProcessSupervisor? workers = null;
        MirrorPulseCloudHostSession? session = null;
        MirrorPulseActiveRemotePoller? remotePoller = null;
        var remoteScheduler = new MirrorPulseInstanceScheduler();
        try
        {
            var configurationStore = new MirrorPulseConfigurationStore(
                Path.Combine(paths.DataRootPath, "config.json"));
            MirrorPulseConfiguration configuration = await configurationStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false) ?? new MirrorPulseConfiguration(
                    MirrorPulseConfiguration.CurrentSchemaVersion,
                    "en-US", false, false, []);
            catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync(cancellationToken)
                .ConfigureAwait(false);
            var rootRouter = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots,
                await catalog.ReadManagedRootNamesAsync(cancellationToken).ConfigureAwait(false));
            var namespaceFence = new MirrorPulseRemoteNamespaceFence(rootRouter, catalog);
            var conflictCenter = new MirrorPulseConflictCenter();
            systemNotifications = new MirrorPulseSystemNotificationPublisher();
            var conflictNotifications = new MirrorPulseConflictNotificationBridge(
                systemNotifications.PublishAsync);
            var credentialStore = new WindowsCredentialManagerStore();
            await new MirrorPulseAdapterInstanceProvisioner(catalog, credentialStore, paths.DataRootPath)
                .RecoverPendingCredentialsAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseCloudHostSession? currentSession = null;
            async ValueTask<MirrorPulseRemotePollApplyOutcome> ApplyCloudRemoteBatchAsync(
                InstanceId instanceId,
                CloudRemoteChangeBatch batch,
                CancellationToken batchCancellationToken)
            {
                await namespaceFence.EnsureApplyAllowedAsync(instanceId, batch, batchCancellationToken).ConfigureAwait(false);
                CloudRemoteApplyResult result = await (currentSession ??
                    throw new InvalidOperationException("The Cloud Files session has not started."))
                    .ApplyRemoteBatchAsync(
                        instanceId, batch, catalog, conflictCenter, conflictNotifications,
                        cancellationToken: batchCancellationToken).ConfigureAwait(false);
                if (result.RequiresRetry)
                {
                    await catalog.SaveInstanceRuntimeStateAsync(
                        new(instanceId, "Remote retry", false, null, "RemoteBatchRetry"),
                        batchCancellationToken).ConfigureAwait(false);
                }
                return MirrorPulseRemotePollApplyOutcome.FromResult(result);
            }

            async ValueTask ApplyRemoteBatchAsync(
                InstanceId instanceId,
                JsonElement payload,
                CancellationToken batchCancellationToken)
            {
                AdapterRemoteChangeBatch adapterBatch = payload.Deserialize<AdapterRemoteChangeBatch>()
                    ?? throw new InvalidDataException("The Adapter remote batch payload is empty.");
                CloudRemoteChangeBatch batch = MirrorPulseAdapterRemoteBatchMapper.Map(
                    instanceId, topology.Roots, adapterBatch);
                await remoteScheduler.RunAsync(instanceId, async token =>
                {
                    // A streamed batch must not overtake the immutable polling intent.
                    if (await catalog.ReadPendingRemoteBatchAsync(instanceId, token).ConfigureAwait(false) is not null)
                        throw new InvalidOperationException("A remote polling batch must converge before streamed changes apply.");
                    return await ApplyCloudRemoteBatchAsync(instanceId, batch, token).ConfigureAwait(false);
                }, batchCancellationToken).ConfigureAwait(false);
            }

            workers = new AdapterInstanceProcessSupervisor(catalog, credentialStore, ApplyRemoteBatchAsync,
                Path.Combine(paths.DataRootPath, "logs", "workers"));
            var directorySource = new MirrorPulseAdapterDirectoryPageSource(workers);
            var provider = new MirrorPulseDemandProvider(rootRouter, workers, directorySource);
            session = MirrorPulseCloudHostSession.CreateDefault(
                paths, topology.Instances, topology.Roots, provider, workers, workers,
                rootRouter, catalog, instanceId => topology.Instances.Any(instance =>
                    instance.InstanceId == instanceId && instance.Enabled),
                conflictCenter, conflictNotifications, workers, remoteScheduler);
            currentSession = session;
            var remoteSnapshotStore = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
            remotePoller = new MirrorPulseActiveRemotePoller(
                directorySource, topology.Instances, topology.Roots, ApplyCloudRemoteBatchAsync,
                snapshotStore: remoteSnapshotStore,
                pendingStore: new MirrorPulseCatalogRemotePollPendingStore(catalog), scheduler: remoteScheduler,
                mayPoll: namespaceFence.CanPollAsync);

            var application = new MirrorPulseHostApplication(
                paths, configurationStore, configuration, hostLease, catalog, conflictCenter, systemNotifications,
                workers, session, remotePoller, remoteScheduler, topology);
            application.InitializeControlPlane(credentialStore);
            return application;
        }
        catch
        {
            if (remotePoller is not null)
            {
                await remotePoller.DisposeAsync().ConfigureAwait(false);
            }

            await remoteScheduler.DisposeAsync().ConfigureAwait(false);

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (workers is not null)
            {
                await workers.DisposeAsync().ConfigureAwait(false);
            }

            if (catalog is not null)
            {
                await catalog.DisposeAsync().ConfigureAwait(false);
            }

            systemNotifications?.Dispose();
            hostLease.Dispose();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is MirrorPulseLifecycleState.Starting or MirrorPulseLifecycleState.Running)
        {
            throw new InvalidOperationException("The MirrorPulse Host is already running.");
        }

        _hostLease.EnsureHeld();
        _state = MirrorPulseLifecycleState.Starting;
        try
        {
            await _session.StartAsync(cancellationToken).ConfigureAwait(false);
            await _workers.StartAsync(_topology).ConfigureAwait(false);
            await _remotePoller.StartAsync(cancellationToken).ConfigureAwait(false);
            _startedAt = DateTimeOffset.UtcNow;
            _requestedAction = null;
            _state = MirrorPulseLifecycleState.Running;
        }
        catch
        {
            _state = MirrorPulseLifecycleState.Failed;
            throw;
        }
    }

    public async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning)
        {
            throw new InvalidOperationException("The MirrorPulse Host must be running before serving control pipes.");
        }

        if (_serveTask is not null)
        {
            throw new InvalidOperationException("The MirrorPulse Host control pipes are already serving.");
        }

        if (_statusPipe is null || _controlPipe is null)
        {
            throw new InvalidOperationException("The MirrorPulse Host control plane is not initialized.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        _serveTask = Task.WhenAll(
            _statusPipe.ServeAsync(linked.Token),
            _controlPipe.ServeAsync(linked.Token));
        await _serveTask.ConfigureAwait(false);
    }

    public async Task<MirrorPulseAppStatusResponse> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        MirrorPulseAdapterTopology current = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
            .ConfigureAwait(false);
        MirrorPulseCloudStatusSnapshot cloud = await _session.ReadStatusAsync(
            current.Instances.Select(instance => instance.InstanceId), cancellationToken)
            .ConfigureAwait(false);
        var entries = new List<MirrorPulseAppInstanceStatus>(current.Instances.Count);
        foreach (var instance in current.Instances)
        {
            MirrorPulseInstanceRuntimeState? runtime = await _catalog.ReadInstanceRuntimeStateAsync(
                instance.InstanceId, cancellationToken).ConfigureAwait(false);
            MirrorPulseInstanceCursorStatus? cursor = cloud.Cursors.SingleOrDefault(item =>
                item.InstanceId == instance.InstanceId);
            entries.Add(new MirrorPulseAppInstanceStatus(instance.InstanceId.ToString(),
                instance.DisplayName, instance.Enabled,
                instance.Enabled ? runtime?.Phase ?? "Not running" : "Offline",
                cursor?.CursorFingerprint, cursor?.UpdatedAt, runtime?.LastSuccessfulSync,
                runtime?.LastErrorCode, runtime?.TransferProgress));
        }

        IReadOnlySet<Guid> snoozed = await _catalog.ReadSnoozedConflictIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<MirrorPulseConflictRecord> uploadConflicts =
            await _catalog.ReadUploadConflictsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        var remoteNotifications = (await _catalog.ReadRemoteConflictProjectionsAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(conflict => cloud.PendingRemoteConflictIds.Contains(conflict.ConflictId))
            .Select(conflict => new MirrorPulseAppNotification(
                conflict.ConflictId.ToString("D"), conflict.RelativePath,
                conflict.DetectedAt, snoozed.Contains(conflict.ConflictId)));
        var notifications = remoteNotifications.Concat(uploadConflicts.Select(conflict =>
                new MirrorPulseAppNotification(conflict.ConflictId.ToString("D"),
                    conflict.RelativePath, conflict.DetectedAt,
                    snoozed.Contains(conflict.ConflictId), MirrorPulseConflictSource.Upload)))
            .OrderByDescending(item => item.DetectedAt)
            .ToArray();
        return new MirrorPulseAppStatusResponse(cloud.PendingUploadCount,
            cloud.PendingRemoteConflictCount, entries, notifications,
            PendingUploadConflicts: uploadConflicts.Count,
            BlockedLocalOperations: await _catalog.ReadBlockedLocalOperationsAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<MirrorPulseControlTopology> ReadTopologyAsync(
        CancellationToken cancellationToken = default)
    {
        MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<MirrorPulseInstanceRuntimeState> runtime =
            await MirrorPulseProductCatalog.ReadRuntimeSnapshotAsync(_paths, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<RootId> deferredRescans = await _catalog.ReadDeferredRescanRootsAsync(cancellationToken).ConfigureAwait(false);
        return new MirrorPulseControlTopology(
            topology.Installations.Select(installation => new MirrorPulseControlInstallation(
                installation.AdapterId.ToString(),
                installation.InstallId.ToString(),
                installation.Version,
                installation.Publisher,
                installation.InstallationDirectory,
                installation.PackageSha256.ToString(),
                installation.Source.ToString(),
                installation.SourceReference,
                installation.IsSigned,
                installation.InstalledAt,
                installation.LifecycleState.ToString(),
                installation.Manifest.MinimumMirrorPulseVersion)).ToArray(),
            topology.Instances.Select(instance => new MirrorPulseControlInstance(
                instance.AdapterId.ToString(),
                instance.InstallId.ToString(),
                instance.InstanceId.ToString(),
                instance.DisplayName,
                instance.Configuration,
                instance.CredentialReferences,
                instance.FileCacheDirectory,
                instance.TransferCacheDirectory,
                instance.Enabled,
                instance.LifecycleState.ToString(),
                instance.WorkerSessionId?.ToString(),
                instance.CreatedAt)).ToArray(),
            topology.Roots.Select(root => new MirrorPulseControlRoot(
                root.AdapterId.ToString(),
                root.InstanceId.ToString(),
                root.RootId.ToString(),
                root.UniquenessKey,
                root.Label,
                root.DirectoryName,
                root.CustomEntry,
                root.State.ToString(),
                root.RegisteredAt)).ToArray(),
            runtime.Select(state => new MirrorPulseControlRuntimeState(
                state.InstanceId.ToString(),
                state.Phase,
                state.RequiresFullRescan || topology.Roots.Any(root => root.InstanceId == state.InstanceId && deferredRescans.Contains(root.RootId)),
                state.LastSuccessfulSync,
                state.LastErrorCode,
                state.TransferProgress?.Operation,
                state.TransferProgress?.BytesTransferred,
                state.TransferProgress?.TotalBytes,
                state.TransferProgress?.UpdatedAt)).ToArray());
    }

    public Task<MirrorPulseControlSettings> ReadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToControlSettings(_configuration));
    }

    public async Task<IReadOnlyList<MirrorPulseControlRootStatus>> ReadRootsAsync(CancellationToken cancellationToken = default)
    {
        MirrorPulseControlTopology topology = await ReadTopologyAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MirrorPulseRootRenameIntent> renames = await _catalog.ReadManagedRootRenamesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RootId> deferred = await _catalog.ReadDeferredRescanRootsAsync(cancellationToken).ConfigureAwait(false);
        return topology.Roots.Select(root =>
        {
            RootId id = RootId.Parse(root.RootId);
            MirrorPulseRootRenameIntent? pending = renames.SingleOrDefault(intent => intent.RootId == id && intent.IsPending);
            var rename = pending is null ? null : new MirrorPulseControlRootRename(pending.OperationId.ToString("D"),
                pending.SourceName, pending.TargetName, pending.Phase.ToString(), pending.UpdatedAt);
            return new MirrorPulseControlRootStatus(root, pending is null ? root.State : "NamespaceRecovery",
                deferred.Contains(id) || pending is not null, rename);
        }).ToArray();
    }

    private void InitializeControlPlane(ISecureCredentialStore credentialStore)
    {
        var provisioner = new MirrorPulseAdapterInstanceProvisioner(
            _catalog, credentialStore, _paths.DataRootPath);
        _statusPipe = new MirrorPulseAppStatusPipe(
            ReadStatusAsync,
            SnoozeConflictAsync,
            SetInstanceEnabledAsync,
            SelectInstallationAsync,
            InstallAdapterAsync,
            (request, cancellationToken) => CreateInstanceAsync(provisioner, request, cancellationToken),
            ResolveConflictAsync);
        var dispatcher = new MirrorPulseControlDispatcher();
        new MirrorPulseLegacyCommandBridge(
            ReadStatusAsync,
            SnoozeConflictAsync,
            ResolveConflictAsync,
            SetInstanceEnabledAsync,
            SelectInstallationAsync,
            InstallAdapterAsync,
            (request, cancellationToken) => CreateInstanceAsync(provisioner, request, cancellationToken),
            _ => Task.FromResult(GetHostStatus()),
            StartRequestedAsync,
            StopRequestedAsync,
            RestartRequestedAsync,
            ReadTopologyAsync,
            ReadSettingsAsync,
            UpdateSettingsAsync,
            CollectDiagnosticsAsync,
            RefreshAsync,
            (instanceId, arguments, cancellationToken) => ConfigureInstanceAsync(provisioner, instanceId,
                arguments, cancellationToken),
            ReadConflictsAsync,
            GetOperationAsync,
            GetOperationAsync,
            CancelOperationAsync,
            RemoveAdapterAsync)
            .Register(dispatcher);
        dispatcher.Register<ControlEmptyArguments, IReadOnlyList<MirrorPulseControlRootStatus>>(
            MirrorPulseControlCommands.RootList, async (_, token) => await ReadRootsAsync(token).ConfigureAwait(false));
        _controlPipe = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync);
    }

    private async Task<MirrorPulseHostStatus> StartRequestedAsync(
        HostLifecycleArguments arguments,
        CancellationToken cancellationToken)
    {
        if (_state == MirrorPulseLifecycleState.Created)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_state == MirrorPulseLifecycleState.Failed)
        {
            throw new InvalidOperationException("The MirrorPulse Host failed during startup and must be restarted.");
        }

        return GetHostStatus();
    }

    private async Task<MirrorPulseControlSettings> UpdateSettingsAsync(
        MirrorPulseSettingsUpdateArguments update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        string locale = update.Locale ?? _configuration.Locale;
        if (!LocaleCode.TryParse(locale, out LocaleCode parsedLocale))
        {
            throw new ArgumentException("The locale is invalid.", nameof(update));
        }

        IReadOnlyList<InstallId> enabledInstallations;
        if (update.EnabledInstallations is null)
        {
            enabledInstallations = _configuration.EnabledInstallations;
        }
        else
        {
            var parsed = new List<InstallId>(update.EnabledInstallations.Count);
            foreach (string value in update.EnabledInstallations)
            {
                if (!InstallId.TryParse(value, out InstallId installId))
                {
                    throw new ArgumentException("Enabled installation IDs must be valid.", nameof(update));
                }

                parsed.Add(installId);
            }

            if (parsed.Distinct().Count() != parsed.Count)
            {
                throw new ArgumentException("Enabled installation IDs must be unique.", nameof(update));
            }

            MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (parsed.Any(id => topology.Installations.All(installation => installation.InstallId != id)))
            {
                throw new ArgumentException("Every enabled installation must be installed.", nameof(update));
            }

            enabledInstallations = parsed;
        }

        string displayName = update.SyncRootDisplayName ?? _configuration.SyncRootDisplayName;
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128 ||
            displayName.Any(character => character < ' '))
        {
            throw new ArgumentException("The sync-root display name is invalid.", nameof(update));
        }

        var next = new MirrorPulseConfiguration(
            MirrorPulseConfiguration.CurrentSchemaVersion,
            parsedLocale.Value,
            update.DeveloperMode ?? _configuration.DeveloperMode,
            update.StartWithWindows ?? _configuration.StartWithWindows,
            enabledInstallations,
            displayName.Trim());
        await _configurationStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _configuration = next;
        return ToControlSettings(next);
    }

    private async Task<MirrorPulseControlDiagnosticsResult> CollectDiagnosticsAsync(
        DiagnosticsArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string outputPath = string.IsNullOrWhiteSpace(arguments.OutputPath)
            ? Path.Combine(_paths.DataRootPath, "diagnostics",
                $"mirrorpulse-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip")
            : Path.GetFullPath(arguments.OutputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Diagnostic packages must use the .zip extension.", nameof(arguments));
        }

        var topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
        var events = new[]
        {
            new DiagnosticEvent(
                Guid.NewGuid(),
                "host",
                new Diagnostic("host.snapshot", "MirrorPulse Host diagnostic snapshot.",
                    DiagnosticSeverity.Information),
                DateTimeOffset.UtcNow,
                properties: new Dictionary<string, string>
                {
                    ["state"] = _state.ToString(),
                    ["processId"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["installationCount"] = topology.Installations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["instanceCount"] = topology.Instances.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
        };
        string logDirectory = Path.Combine(_paths.DataRootPath, "logs");
        IReadOnlyList<string> logs = arguments.IncludeLogs && Directory.Exists(logDirectory)
            ? Directory.EnumerateFiles(logDirectory, "mirrorpulse.log*").ToArray()
            : [];
        string packagePath = await DiagnosticPackageExporter.ExportAsync(
            outputPath, events, logs, cancellationToken).ConfigureAwait(false);
        return new MirrorPulseControlDiagnosticsResult(
            packagePath, DateTimeOffset.UtcNow, arguments.IncludeLogs);
    }

    private static MirrorPulseControlSettings ToControlSettings(MirrorPulseConfiguration configuration) =>
        new(configuration.SchemaVersion, configuration.Locale, configuration.DeveloperMode,
            configuration.StartWithWindows,
            configuration.EnabledInstallations.Select(installId => installId.ToString()).ToArray(),
            configuration.SyncRootDisplayName);

    private Task<MirrorPulseHostStatus> StopRequestedAsync(
        HostLifecycleArguments arguments,
        CancellationToken cancellationToken) =>
        RequestStopAsync("stop", arguments, cancellationToken);

    private Task<MirrorPulseHostStatus> RestartRequestedAsync(
        HostLifecycleArguments arguments,
        CancellationToken cancellationToken) =>
        RequestStopAsync("restart", arguments, cancellationToken);

    private Task<MirrorPulseHostStatus> RequestStopAsync(
        string action,
        HostLifecycleArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRunning && _state != MirrorPulseLifecycleState.Stopping)
        {
            throw new InvalidOperationException("The MirrorPulse Host is not running.");
        }

        _requestedAction = action;
        _state = MirrorPulseLifecycleState.Stopping;
        _ = CancelAfterResponseAsync();
        return Task.FromResult(GetHostStatus());
    }

    private async Task CancelAfterResponseAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<MirrorPulseAppStatusResponse> InstallAdapterAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(packagePath.Trim());
        if (!string.Equals(Path.GetExtension(source), ".mpadapter", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Host install command accepts only .mpadapter files.", nameof(packagePath));
        }

        string signaturePath = source + ".signature.json";
        string runtimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "win-x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The Adapter process architecture is unsupported."),
        };
        string installationRoot = Path.Combine(_paths.DataRootPath, "adapters", "installed");
        InstalledAdapter installed = await _catalog.InstallSignedAdapterAsync(
            source, signaturePath, installationRoot, runtimeIdentifier, cancellationToken)
            .ConfigureAwait(false);
        return (await ReadStatusAsync(cancellationToken).ConfigureAwait(false)) with
        {
            InstalledAdapterId = installed.InstallId.ToString(),
        };
    }

    private async Task<MirrorPulseAppStatusResponse> RefreshAsync(
        SyncRefreshArguments arguments,
        CancellationToken cancellationToken)
    {
        MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (AdapterInstance instance in topology.Instances.Where(item => item.Enabled))
        {
            await _remotePoller.PollOnceAsync(instance.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseAppStatusResponse> CreateInstanceAsync(
        MirrorPulseAdapterInstanceProvisioner provisioner,
        MirrorPulseCreateInstanceRequest request,
        CancellationToken cancellationToken)
    {
        AdapterInstance instance = await provisioner.CreateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return (await ReadStatusAsync(cancellationToken).ConfigureAwait(false)) with
        {
            CreatedInstanceId = instance.InstanceId.ToString(),
        };
    }

    private async Task<MirrorPulseAppStatusResponse> ConfigureInstanceAsync(
        MirrorPulseAdapterInstanceProvisioner provisioner,
        InstanceId instanceId,
        InstanceConfigureArguments arguments,
        CancellationToken cancellationToken)
    {
        await provisioner.ConfigureAsync(instanceId, arguments.DisplayName,
            arguments.Configuration, arguments.RootLabels, arguments.Secret, arguments.RemoveCredential,
            cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseControlConflictList> ReadConflictsAsync(
        ConflictListArguments arguments,
        CancellationToken cancellationToken)
    {
        var conflicts = (await _catalog.ReadRemoteConflictProjectionsAsync(cancellationToken)
                .ConfigureAwait(false))
            .Concat(await _catalog.ReadUploadConflictsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .OrderByDescending(item => item.DetectedAt)
            .Select(ToControlConflict)
            .ToList();
        int limit = arguments.Limit is > 0 and <= 1000 ? arguments.Limit.Value : 100;
        return new(conflicts.Take(limit).ToArray(), conflicts.Count > limit
            ? limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static MirrorPulseControlConflict ToControlConflict(MirrorPulseConflictRecord conflict) =>
        new(conflict.ConflictId.ToString("D"), conflict.InstanceId.ToString(), conflict.RelativePath,
            conflict.Reason.ToString(), conflict.Status.ToString(), conflict.Source.ToString(),
            conflict.LocalRevision, conflict.RemoteRevision, conflict.DetectedAt);

    private async Task<MirrorPulseControlOperation> GetOperationAsync(
        OperationIdArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(arguments.OperationId, out Guid id) || id == Guid.Empty)
            throw new ArgumentException("The operation ID is invalid.", nameof(arguments));
        MirrorPulseUserCommandRecord record = await _catalog.ReadUserCommandAsync(id, cancellationToken)
            .ConfigureAwait(false) ?? throw new FileNotFoundException("The operation was not found.");
        return new(record.CommandId.ToString("D"), record.Action, record.TargetId, record.State);
    }

    private async Task<MirrorPulseControlOperation> CancelOperationAsync(
        OperationIdArguments arguments,
        CancellationToken cancellationToken)
    {
        MirrorPulseControlOperation current = await GetOperationAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (current.State is "completed" or "failed" or "cancelled")
            return current;
        Guid id = Guid.Parse(current.OperationId);
        await _catalog.SaveUserCommandAsync(new MirrorPulseUserCommandRecord(
            id, current.Action, current.TargetId, "cancelled"), cancellationToken)
            .ConfigureAwait(false);
        return current with { State = "cancelled" };
    }

    private async Task<MirrorPulseAppStatusResponse> RemoveAdapterAsync(
        AdapterRemoveArguments arguments,
        CancellationToken cancellationToken)
    {
        MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken)
            .ConfigureAwait(false);
        InstallId installId;
        if (arguments.InstallId is not null)
        {
            installId = InstallId.Parse(arguments.InstallId);
        }
        else if (InstallId.TryParse(arguments.AdapterId, out InstallId direct))
        {
            installId = direct;
        }
        else if (AdapterId.TryParse(arguments.AdapterId, out AdapterId adapterId))
        {
            InstalledAdapter[] matches = topology.Installations.Where(item => item.AdapterId == adapterId).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Specify --install-id when an Adapter has multiple installations.");
            installId = matches[0].InstallId;
        }
        else
        {
            throw new ArgumentException("The Adapter or installation ID is invalid.", nameof(arguments));
        }

        InstalledAdapter installation = topology.Installations.SingleOrDefault(item => item.InstallId == installId)
            ?? throw new FileNotFoundException("The selected Adapter installation is not registered.");
        if (topology.Instances.Any(instance => instance.InstallId == installId))
            throw new InvalidOperationException("The Adapter installation is still referenced by an instance.");
        var pathProvider = new CurrentUserAdapterPathProvider(
            Path.Combine(_paths.DataRootPath, "adapters", "installed"));
        var uninstall = new AdapterUninstallService(pathProvider,
            new AdapterActivationPointerStore(pathProvider));
        await uninstall.UninstallAsync(installation, cancellationToken).ConfigureAwait(false);
        await _catalog.RemoveInstallationAsync(installId, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseAppStatusResponse> SnoozeConflictAsync(
        Guid conflictId,
        CancellationToken cancellationToken)
    {
        MirrorPulseAppStatusResponse current = await ReadStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!current.Notifications.Any(item => item.ConflictId == conflictId.ToString("D")))
        {
            throw new FileNotFoundException("The pending conflict notification was not found.");
        }

        await _catalog.SetConflictSnoozedAsync(conflictId, true, cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseAppStatusResponse> SetInstanceEnabledAsync(
        InstanceId instanceId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _catalog.SetInstanceEnabledAsync(instanceId, enabled, cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseAppStatusResponse> SelectInstallationAsync(
        InstanceId instanceId,
        InstallId installId,
        CancellationToken cancellationToken)
    {
        await _catalog.SelectInstanceInstallationAsync(instanceId, installId, cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MirrorPulseAppStatusResponse> ResolveConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken)
    {
        MirrorPulseConflictCommandResult command;
        if (await _catalog.ReadUploadConflictAsync(conflictId, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            MirrorPulseConflictResolution resolution = await _session.ApplyUploadConflictAsync(conflictId, action, cancellationToken)
                .ConfigureAwait(false);
            MirrorPulseUserCommandRecord record = await _catalog.ReadUserCommandAsync(resolution.ResolutionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The conflict command outcome was not persisted.");
            command = new(resolution.ResolutionId, conflictId, record.State, resolution.PreservedPath);
            if (command.State == "resolved") _conflictCenter.Remove(conflictId);
        }
        else
        {
            MirrorPulseRemoteConflictActionOutcome outcome =
                await _session.ApplyRemoteConflictAsync(conflictId, action, _catalog,
                    cancellationToken).ConfigureAwait(false);
            if (outcome.Resolved)
            {
                _conflictCenter.Remove(conflictId);
            }
            command = new(outcome.CommandId, conflictId, outcome.Resolved ? "resolved" : outcome.CommandQueued ? "pending" : "failed",
                outcome.PreservedPath, !outcome.Resolved && !outcome.CommandQueued ? "ConflictApplyFailed" : null);
        }

        return (await ReadStatusAsync(cancellationToken).ConfigureAwait(false)) with { ConflictCommand = command };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _state = MirrorPulseLifecycleState.Stopping;
        _shutdown.Cancel();
        if (_serveTask is not null)
        {
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _remotePoller.DisposeAsync().ConfigureAwait(false);
        await _remoteScheduler.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
        await _workers.DisposeAsync().ConfigureAwait(false);
        await _catalog.DisposeAsync().ConfigureAwait(false);
        _systemNotifications.Dispose();
        _shutdown.Dispose();
        _hostLease.Dispose();
        _state = MirrorPulseLifecycleState.Stopped;
        _disposed = true;
    }
}
