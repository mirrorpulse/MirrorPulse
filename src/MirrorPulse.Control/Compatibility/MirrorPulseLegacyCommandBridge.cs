using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Control.Compatibility;

/// <summary>
/// Exposes the current Host status handlers through the versioned control contract.
/// </summary>
public sealed class MirrorPulseLegacyCommandBridge
{
    private readonly Func<CancellationToken, Task<MirrorPulseAppStatusResponse>> _readStatus;
    private readonly Func<Guid, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _snooze;
    private readonly Func<Guid, MirrorPulseConflictAction, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _resolveConflict;
    private readonly Func<InstanceId, bool, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _setEnabled;
    private readonly Func<InstanceId, InstallId, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _selectVersion;
    private readonly Func<string, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _install;
    private readonly Func<MirrorPulseCreateInstanceRequest, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _createInstance;
    private readonly Func<InstanceId, InstanceConfigureArguments, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _configureInstance;
    private readonly Func<ConflictListArguments, CancellationToken,
        Task<MirrorPulseControlConflictList>>? _listConflicts;
    private readonly Func<OperationIdArguments, CancellationToken,
        Task<MirrorPulseControlOperation>>? _getOperation;
    private readonly Func<OperationIdArguments, CancellationToken,
        Task<MirrorPulseControlOperation>>? _watchOperation;
    private readonly Func<OperationIdArguments, CancellationToken,
        Task<MirrorPulseControlOperation>>? _cancelOperation;
    private readonly Func<AdapterRemoveArguments, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _removeAdapter;
    private readonly Func<NamespaceMemberMoveArguments, CancellationToken,
        Task<MirrorPulseControlNamespaceMemberMoveResult>>? _moveMember;
    private readonly Func<NamespaceBirthRecoverArguments, CancellationToken,
        Task<MirrorPulseControlNamespaceBirthRecoveryResult>>? _recoverBirth;
    private readonly Func<CancellationToken, Task<MirrorPulseHostStatus>>? _hostStatus;
    private readonly Func<HostLifecycleArguments, CancellationToken,
        Task<MirrorPulseHostStatus>>? _hostStart;
    private readonly Func<HostLifecycleArguments, CancellationToken,
        Task<MirrorPulseHostStatus>>? _hostStop;
    private readonly Func<HostLifecycleArguments, CancellationToken,
        Task<MirrorPulseHostStatus>>? _hostRestart;
    private readonly Func<CancellationToken, Task<MirrorPulseControlTopology>>? _topology;
    private readonly Func<CancellationToken, Task<MirrorPulseControlSettings>>? _settingsGet;
    private readonly Func<MirrorPulseSettingsUpdateArguments, CancellationToken,
        Task<MirrorPulseControlSettings>>? _settingsSet;
    private readonly Func<DiagnosticsArguments, CancellationToken,
        Task<MirrorPulseControlDiagnosticsResult>>? _diagnostics;
    private readonly Func<SyncRefreshArguments, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _refresh;

    public MirrorPulseLegacyCommandBridge(
        Func<CancellationToken, Task<MirrorPulseAppStatusResponse>> readStatus,
        Func<Guid, CancellationToken, Task<MirrorPulseAppStatusResponse>>? snooze = null,
        Func<Guid, MirrorPulseConflictAction, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? resolveConflict = null,
        Func<InstanceId, bool, CancellationToken, Task<MirrorPulseAppStatusResponse>>? setEnabled = null,
        Func<InstanceId, InstallId, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? selectVersion = null,
        Func<string, CancellationToken, Task<MirrorPulseAppStatusResponse>>? install = null,
        Func<MirrorPulseCreateInstanceRequest, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? createInstance = null,
        Func<CancellationToken, Task<MirrorPulseHostStatus>>? hostStatus = null,
        Func<HostLifecycleArguments, CancellationToken,
            Task<MirrorPulseHostStatus>>? hostStart = null,
        Func<HostLifecycleArguments, CancellationToken,
            Task<MirrorPulseHostStatus>>? hostStop = null,
        Func<HostLifecycleArguments, CancellationToken,
            Task<MirrorPulseHostStatus>>? hostRestart = null,
        Func<CancellationToken, Task<MirrorPulseControlTopology>>? topology = null,
        Func<CancellationToken, Task<MirrorPulseControlSettings>>? settingsGet = null,
        Func<MirrorPulseSettingsUpdateArguments, CancellationToken,
            Task<MirrorPulseControlSettings>>? settingsSet = null,
        Func<DiagnosticsArguments, CancellationToken,
            Task<MirrorPulseControlDiagnosticsResult>>? diagnostics = null,
        Func<SyncRefreshArguments, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? refresh = null,
        Func<InstanceId, InstanceConfigureArguments, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? configureInstance = null,
        Func<ConflictListArguments, CancellationToken,
            Task<MirrorPulseControlConflictList>>? listConflicts = null,
        Func<OperationIdArguments, CancellationToken,
            Task<MirrorPulseControlOperation>>? getOperation = null,
        Func<OperationIdArguments, CancellationToken,
            Task<MirrorPulseControlOperation>>? watchOperation = null,
        Func<OperationIdArguments, CancellationToken,
            Task<MirrorPulseControlOperation>>? cancelOperation = null,
        Func<AdapterRemoveArguments, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? removeAdapter = null,
        Func<NamespaceMemberMoveArguments, CancellationToken,
            Task<MirrorPulseControlNamespaceMemberMoveResult>>? moveMember = null,
        Func<NamespaceBirthRecoverArguments, CancellationToken,
            Task<MirrorPulseControlNamespaceBirthRecoveryResult>>? recoverBirth = null)
    {
        _readStatus = readStatus ?? throw new ArgumentNullException(nameof(readStatus));
        _snooze = snooze;
        _resolveConflict = resolveConflict;
        _setEnabled = setEnabled;
        _selectVersion = selectVersion;
        _install = install;
        _createInstance = createInstance;
        _configureInstance = configureInstance;
        _hostStatus = hostStatus;
        _hostStart = hostStart;
        _hostStop = hostStop;
        _hostRestart = hostRestart;
        _topology = topology;
        _settingsGet = settingsGet;
        _settingsSet = settingsSet;
        _diagnostics = diagnostics;
        _refresh = refresh;
        _listConflicts = listConflicts;
        _getOperation = getOperation;
        _watchOperation = watchOperation;
        _cancelOperation = cancelOperation;
        _removeAdapter = removeAdapter;
        _moveMember = moveMember;
        _recoverBirth = recoverBirth;
    }

    public void Register(MirrorPulseControlDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Register<ControlEmptyArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.SyncStatus,
            (_, cancellationToken) => new(_readStatus(cancellationToken)));

        if (_refresh is not null)
        {
            dispatcher.Register<SyncRefreshArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.SyncRefresh,
                (arguments, cancellationToken) => new(_refresh(arguments, cancellationToken)));
        }

        if (_hostStatus is not null)
        {
            dispatcher.Register<ControlEmptyArguments, MirrorPulseHostStatus>(
                MirrorPulseControlCommands.HostStatus,
                (_, cancellationToken) => new(_hostStatus(cancellationToken)));
        }

        if (_hostStart is not null)
        {
            dispatcher.Register<HostLifecycleArguments, MirrorPulseHostStatus>(
                MirrorPulseControlCommands.HostStart,
                (arguments, cancellationToken) => new(_hostStart(arguments, cancellationToken)));
        }

        if (_hostStop is not null)
        {
            dispatcher.Register<HostLifecycleArguments, MirrorPulseHostStatus>(
                MirrorPulseControlCommands.HostStop,
                (arguments, cancellationToken) => new(_hostStop(arguments, cancellationToken)));
        }

        if (_hostRestart is not null)
        {
            dispatcher.Register<HostLifecycleArguments, MirrorPulseHostStatus>(
                MirrorPulseControlCommands.HostRestart,
                (arguments, cancellationToken) => new(_hostRestart(arguments, cancellationToken)));
        }

        if (_topology is not null)
        {
            dispatcher.Register<ControlEmptyArguments, MirrorPulseControlTopology>(
                MirrorPulseControlCommands.AdapterList,
                (_, cancellationToken) => new(_topology(cancellationToken)));
            dispatcher.Register<InstanceListArguments, MirrorPulseControlTopology>(
                MirrorPulseControlCommands.InstanceList,
                (_, cancellationToken) => new(_topology(cancellationToken)));
        }

        if (_listConflicts is not null)
        {
            dispatcher.Register<ConflictListArguments, MirrorPulseControlConflictList>(
                MirrorPulseControlCommands.ConflictList,
                (arguments, cancellationToken) => new(_listConflicts(arguments, cancellationToken)));
        }

        RegisterOperation(dispatcher, MirrorPulseControlCommands.OperationGet, _getOperation);
        RegisterOperation(dispatcher, MirrorPulseControlCommands.OperationWatch, _watchOperation);
        RegisterOperation(dispatcher, MirrorPulseControlCommands.OperationCancel, _cancelOperation);

        if (_removeAdapter is not null)
        {
            dispatcher.Register<AdapterRemoveArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.AdapterRemove,
                (arguments, cancellationToken) => new(_removeAdapter(arguments, cancellationToken)));
        }

        if (_moveMember is not null)
        {
            dispatcher.Register<NamespaceMemberMoveArguments, MirrorPulseControlNamespaceMemberMoveResult>(
                MirrorPulseControlCommands.NamespaceMemberMove,
                (arguments, cancellationToken) => new(_moveMember(arguments, cancellationToken)));
        }

        if (_recoverBirth is not null)
        {
            dispatcher.Register<NamespaceBirthRecoverArguments, MirrorPulseControlNamespaceBirthRecoveryResult>(
                MirrorPulseControlCommands.NamespaceBirthRecover,
                (arguments, cancellationToken) => new(_recoverBirth(arguments, cancellationToken)));
        }

        if (_settingsGet is not null)
        {
            dispatcher.Register<ControlEmptyArguments, MirrorPulseControlSettings>(
                MirrorPulseControlCommands.SettingsGet,
                (_, cancellationToken) => new(_settingsGet(cancellationToken)));
        }

        if (_settingsSet is not null)
        {
            dispatcher.Register<MirrorPulseSettingsUpdateArguments, MirrorPulseControlSettings>(
                MirrorPulseControlCommands.SettingsSet,
                (arguments, cancellationToken) => new(_settingsSet(arguments, cancellationToken)));
        }

        if (_diagnostics is not null)
        {
            dispatcher.Register<DiagnosticsArguments, MirrorPulseControlDiagnosticsResult>(
                MirrorPulseControlCommands.DiagnosticsCollect,
                (arguments, cancellationToken) => new(_diagnostics(arguments, cancellationToken)));
        }

        if (_snooze is not null)
        {
            dispatcher.Register<ConflictSnoozeArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.ConflictSnooze,
                (arguments, cancellationToken) => new(_snooze(
                    RequireId(arguments.ConflictId, nameof(arguments.ConflictId)), cancellationToken)));
        }

        if (_resolveConflict is not null)
        {
            dispatcher.Register<ConflictResolveArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.ConflictResolve,
                (arguments, cancellationToken) => new(_resolveConflict(
                    RequireId(arguments.ConflictId, nameof(arguments.ConflictId)),
                    RequireAction(arguments.Action), cancellationToken)));
        }

        if (_setEnabled is not null)
        {
            dispatcher.Register<InstanceEnableArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.InstanceEnable,
                (arguments, cancellationToken) => new(_setEnabled(
                    InstanceId.Parse(arguments.InstanceId), arguments.Enabled, cancellationToken)));
        }

        if (_selectVersion is not null)
        {
            dispatcher.Register<InstanceSelectVersionArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.InstanceSelectVersion,
                (arguments, cancellationToken) => new(_selectVersion(
                    InstanceId.Parse(arguments.InstanceId), InstallId.Parse(arguments.InstallId), cancellationToken)));
        }

        if (_install is not null)
        {
            dispatcher.Register<AdapterInstallArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.AdapterInstall,
                (arguments, cancellationToken) => new(_install(
                    RequireText(arguments.PackagePath, nameof(arguments.PackagePath)), cancellationToken)));
        }

        if (_createInstance is not null)
        {
            dispatcher.Register<InstanceCreateArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.InstanceCreate,
                (arguments, cancellationToken) => new(_createInstance(
                    new MirrorPulseCreateInstanceRequest(
                        InstallId.Parse(arguments.InstallId).ToString(),
                        RequireText(arguments.DisplayName, nameof(arguments.DisplayName)),
                        arguments.Configuration,
                        arguments.RootLabels,
                        arguments.Secret,
                        arguments.Enabled),
                    cancellationToken)));
        }

        if (_configureInstance is not null)
        {
            dispatcher.Register<InstanceConfigureArguments, MirrorPulseAppStatusResponse>(
                MirrorPulseControlCommands.InstanceConfigure,
                (arguments, cancellationToken) => new(_configureInstance(
                    InstanceId.Parse(arguments.InstanceId), arguments, cancellationToken)));
        }
    }

    private static Guid RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", name);
        }

        return value;
    }

    private static MirrorPulseConflictAction RequireAction(MirrorPulseConflictAction action)
    {
        if (action == MirrorPulseConflictAction.Defer)
        {
            throw new ArgumentException("Defer is not a conflict resolution action.", nameof(action));
        }

        return action;
    }

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", name);
        }

        return value.Trim();
    }

    private static void RegisterOperation(
        MirrorPulseControlDispatcher dispatcher,
        string command,
        Func<OperationIdArguments, CancellationToken, Task<MirrorPulseControlOperation>>? handler)
    {
        if (handler is null) return;
        dispatcher.Register<OperationIdArguments, MirrorPulseControlOperation>(
            command, (arguments, cancellationToken) => new(handler(arguments, cancellationToken)));
    }
}
