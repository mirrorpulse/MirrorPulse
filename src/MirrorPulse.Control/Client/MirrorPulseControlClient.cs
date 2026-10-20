using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Control.Client;

/// <summary>
/// Options for a current-user control client.
/// </summary>
public sealed record MirrorPulseControlClientOptions
{
    public string? PipeName { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int? ExpectedHostProcessId { get; init; }
    public Func<string>? GetExpectedHostExecutablePath { get; init; }

    /// <summary>
    /// Starts the current-user Host after an initial connection failure.
    /// </summary>
    public Func<CancellationToken, Task>? EnsureHostStartedAsync { get; init; }

    internal void Validate()
    {
        if (ConnectTimeout < TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout),
                "Control client timeouts must be positive.");
        }
    }
}

/// <summary>
/// A structured failure returned by the control client.
/// </summary>
public sealed class MirrorPulseControlException : Exception
{
    public MirrorPulseControlException(ControlError error, Guid? requestId = null)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error)))
    {
        Error = error;
        RequestId = requestId;
    }

    public ControlError Error { get; }

    public Guid? RequestId { get; }
}

/// <summary>
/// Sends typed requests to the current-user Host control pipe.
/// </summary>
public sealed class MirrorPulseControlClient
{
    public Task<MirrorPulseControlNamespaceInitialization> InitializeNamespaceProtectionAsync(Guid operationId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceProtectionInitializeArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionInitialize, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceInitialization> GetNamespaceInitializationStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceProtectionInitializationStatusArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionInitializationStatus, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceInitialization> RecoverNamespaceCurrentTreeAsync(Guid operationId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceProtectionRecoverCurrentArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverCurrent, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceInitialization> RecoverNamespaceRootAsync(Guid operationId, Guid rootId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceProtectionRecoverRootArguments, MirrorPulseControlNamespaceInitialization>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverRoot, new(operationId, rootId), cancellationToken);

    public Task<IReadOnlyList<MirrorPulseControlNamespaceInitialization>> GetNamespaceInitializationsAsync(
        Guid? afterOperationId = null, int limit = 256, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceProtectionInitializationsArguments, IReadOnlyList<MirrorPulseControlNamespaceInitialization>>(
            MirrorPulseControlCommands.NamespaceProtectionInitializations, new(afterOperationId, limit), cancellationToken);

    public Task<IReadOnlyList<MirrorPulseControlPermissionCapture>> GetNamespacePermissionCapturesAsync(Guid rootId,
        Guid? afterManifestId = null, int limit = 256, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceProtectionCapturesArguments, IReadOnlyList<MirrorPulseControlPermissionCapture>>(
            MirrorPulseControlCommands.NamespaceProtectionCaptures, new(rootId, afterManifestId, limit), cancellationToken);

    public Task<MirrorPulseControlOriginalTreeRecovery> RecoverNamespaceOriginalTreeAsync(Guid manifestId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceProtectionRecoverTreeArguments, MirrorPulseControlOriginalTreeRecovery>(
            MirrorPulseControlCommands.NamespaceProtectionRecoverTree, new(manifestId), cancellationToken);

    private readonly MirrorPulseControlClientOptions _options;

    /// <summary>Waits within the existing request budget while lifecycle queries and stop remain available.</summary>
    /// <remarks>A timeout leaves the Host's initialization and durable recovery ownership intact.</remarks>
    public async Task<MirrorPulseHostStatus> WaitForHostReadyAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                var status = await GetHostStatusAsync(stop.Token).ConfigureAwait(false);
                if (status.State is "Running" or "Degraded") return status;
                if (status.State is not ("Created" or "Starting"))
                    throw new MirrorPulseControlException(new ControlError(MirrorPulseControlErrorCodes.HostStartFailed,
                        "The Host stopped or failed during initialization.", MirrorPulse.Core.Contracts.ErrorCategory.Native, retryable: true));
                await Task.Delay(TimeSpan.FromMilliseconds(100), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new MirrorPulseControlException(new ControlError(MirrorPulseControlErrorCodes.RequestTimeout,
                "The Host has not completed initialization within the request timeout. Its lifecycle status remains available.",
                MirrorPulse.Core.Contracts.ErrorCategory.Network, retryable: true));
        }
    }
    private readonly string _pipeName;

    public MirrorPulseControlClient(MirrorPulseControlClientOptions? options = null)
    {
        _options = options ?? new MirrorPulseControlClientOptions();
        _options.Validate();
        _pipeName = string.IsNullOrWhiteSpace(_options.PipeName)
            ? MirrorPulseControlPipeNames.CurrentUserV1()
            : _options.PipeName.Trim();
    }

    public string PipeName => _pipeName;

    public async Task<ControlResponseEnvelope> SendEnvelopeAsync<TArguments>(
        string command,
        TArguments arguments,
        CancellationToken cancellationToken = default)
        where TArguments : IMirrorPulseControlArguments
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A control command is required.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(arguments);
        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            using var argumentsDocument = JsonDocument.Parse(
                MirrorPulseControlJsonCodec.Serialize(arguments));
            var request = new ControlRequestEnvelope(
                MirrorPulseControlSchema.CurrentVersion,
                Guid.NewGuid(),
                command,
                argumentsDocument.RootElement,
                typeof(MirrorPulseControlClient).Assembly.GetName().Version?.ToString());

            await using var pipe = await ConnectAsync(linked.Token).ConfigureAwait(false);
            NamedPipePeerIdentity.ValidateServer(pipe, _options.ExpectedHostProcessId);
            if (_options.GetExpectedHostExecutablePath is not null)
            {
                NamedPipePeerIdentity.ValidateServerImage(pipe, _options.GetExpectedHostExecutablePath());
            }
            await MirrorPulseControlPipeTransport.WriteFrameAsync(
                pipe,
                MirrorPulseControlJsonCodec.Serialize(request),
                linked.Token).ConfigureAwait(false);
            var response = MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(
                await MirrorPulseControlPipeTransport.ReadFrameAsync(pipe, linked.Token)
                    .ConfigureAwait(false));
            if (response.RequestId != request.RequestId)
            {
                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.InvalidRequest,
                        "The Host returned a response for a different request.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Protocol),
                    request.RequestId);
            }

            if (!response.Succeeded)
            {
                throw new MirrorPulseControlException(response.Error!, request.RequestId);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.RequestTimeout,
                    "The control request timed out.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Network,
                    retryable: true));
        }
        catch (InvalidDataException)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.InvalidRequest,
                    "The Host returned an invalid control response.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Protocol));
        }
        catch (UnauthorizedAccessException)
        {
            throw new MirrorPulseControlException(new ControlError(MirrorPulseControlErrorCodes.Unauthorized,
                "The control pipe peer identity could not be verified.", MirrorPulse.Core.Contracts.ErrorCategory.Authorization));
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.HostUnavailable,
                    "The MirrorPulse Host disconnected before completing the request.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Network,
                    retryable: true));
        }
    }

    public async Task<TResponse> SendAsync<TArguments, TResponse>(
        string command,
        TArguments arguments,
        CancellationToken cancellationToken = default)
        where TArguments : IMirrorPulseControlArguments
    {
        var response = await SendEnvelopeAsync(command, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (response.Data is not { } data)
        {
            throw new MirrorPulseControlException(
                new ControlError(
                    MirrorPulseControlErrorCodes.InvalidRequest,
                    "The Host returned no result data.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Protocol),
                response.RequestId);
        }

        return MirrorPulseControlJsonCodec.Deserialize<TResponse>(
            System.Text.Encoding.UTF8.GetBytes(data.GetRawText()));
    }

    public Task<MirrorPulseAppStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ControlEmptyArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.SyncStatus, new(), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<SyncRefreshArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.SyncRefresh, new(force), cancellationToken);

    public Task<MirrorPulseHostStatus> GetHostStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<ControlEmptyArguments, MirrorPulseHostStatus>(
            MirrorPulseControlCommands.HostStatus, new(), cancellationToken);

    public Task<MirrorPulseHostStatus> StartHostAsync(
        bool force = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<HostLifecycleArguments, MirrorPulseHostStatus>(
            MirrorPulseControlCommands.HostStart, new(force), cancellationToken);

    public Task<MirrorPulseHostStatus> StopHostAsync(
        bool force = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<HostLifecycleArguments, MirrorPulseHostStatus>(
            MirrorPulseControlCommands.HostStop, new(force), cancellationToken);

    public Task<MirrorPulseHostStatus> RestartHostAsync(
        bool force = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<HostLifecycleArguments, MirrorPulseHostStatus>(
            MirrorPulseControlCommands.HostRestart, new(force), cancellationToken);

    public Task<MirrorPulseControlTopology> GetAdapterTopologyAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<ControlEmptyArguments, MirrorPulseControlTopology>(
            MirrorPulseControlCommands.AdapterList, new(), cancellationToken);

    public Task<MirrorPulseControlTopology> GetInstancesAsync(
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<InstanceListArguments, MirrorPulseControlTopology>(
            MirrorPulseControlCommands.InstanceList,
            new(limit, cursor), cancellationToken);

    public Task<IReadOnlyList<MirrorPulseControlRootStatus>> GetRootsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ControlEmptyArguments, IReadOnlyList<MirrorPulseControlRootStatus>>(
            MirrorPulseControlCommands.RootList, new(), cancellationToken);

    public Task<MirrorPulseControlRootRecovery> RecoverRootRenameAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<RootRecoverArguments, MirrorPulseControlRootRecovery>(
            MirrorPulseControlCommands.RootRecover, new(operationId), cancellationToken);

    public Task<MirrorPulseControlRootRenamePreparation> PrepareRootRenameAsync(Guid rootId, string label,
        CancellationToken cancellationToken = default) =>
        SendAsync<RootPrepareRenameArguments, MirrorPulseControlRootRenamePreparation>(
            MirrorPulseControlCommands.RootPrepareRename, new(rootId, label), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberCreation> CreateNamespaceMemberAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory = false, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberCreateArguments, MirrorPulseControlNamespaceMemberCreation>(
            MirrorPulseControlCommands.NamespaceMemberCreate, new(operationId, parentMemberId, name, isDirectory), cancellationToken);

    public Task<MirrorPulseControlNamespaceImportPreparation> PrepareNamespaceImportAsync(Guid operationId, Guid parentMemberId,
        string name, string sourcePath, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberPrepareImportArguments, MirrorPulseControlNamespaceImportPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareImport, new(operationId, parentMemberId, name, sourcePath), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberCreation> ExecuteNamespaceImportAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberImportArguments, MirrorPulseControlNamespaceMemberCreation>(
            MirrorPulseControlCommands.NamespaceMemberImport, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceImportStatus> GetNamespaceImportStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberImportStatusArguments, MirrorPulseControlNamespaceImportStatus>(
            MirrorPulseControlCommands.NamespaceMemberImportStatus, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceReplacementPreparation> PrepareNamespaceReplacementAsync(Guid operationId, Guid memberId,
        string sourcePath, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberPrepareReplaceArguments, MirrorPulseControlNamespaceReplacementPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareReplace, new(operationId, memberId, sourcePath), cancellationToken);

    public Task<MirrorPulseControlNamespaceReplacementCompletion> ExecuteNamespaceReplacementAsync(Guid operationId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceMemberReplaceArguments, MirrorPulseControlNamespaceReplacementCompletion>(
            MirrorPulseControlCommands.NamespaceMemberReplace, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceReplacementStatus> GetNamespaceReplacementStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default) => SendAsync<NamespaceMemberReplacementStatusArguments, MirrorPulseControlNamespaceReplacementStatus>(
            MirrorPulseControlCommands.NamespaceMemberReplacementStatus, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberInspection> InspectNamespaceMemberAsync(string relativePath, bool isDirectory = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberInspectArguments, MirrorPulseControlNamespaceMemberInspection>(
            MirrorPulseControlCommands.NamespaceMemberInspect, new(relativePath, isDirectory), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberMovePreparation> PrepareNamespaceMemberMoveAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string targetName, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberPrepareMoveArguments, MirrorPulseControlNamespaceMemberMovePreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareMove, new(operationId, memberId, targetParentMemberId, targetName), cancellationToken);

    public Task<IReadOnlyList<MirrorPulseControlNamespaceMemberTreeEntry>> GetNamespaceMemberTreeAsync(Guid memberId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberTreeArguments, IReadOnlyList<MirrorPulseControlNamespaceMemberTreeEntry>>(
            MirrorPulseControlCommands.NamespaceMemberTree, new(memberId), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberMoveResult> ExecuteNamespaceMemberMoveAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberMoveArguments, MirrorPulseControlNamespaceMemberMoveResult>(
            MirrorPulseControlCommands.NamespaceMemberMove, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberMoveStatus> GetNamespaceMemberMoveStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberMoveStatusArguments, MirrorPulseControlNamespaceMemberMoveStatus>(
            MirrorPulseControlCommands.NamespaceMemberMoveStatus, new(operationId), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementPreparation> PrepareNamespaceMemberRetirementAsync(Guid operationId,
        Guid memberId, CancellationToken cancellationToken = default) =>
        PrepareNamespaceMemberRetirementAsync(operationId, memberId, false, cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementPreparation> PrepareNamespaceMemberRetirementAsync(Guid operationId,
        Guid memberId, bool recursive, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberPrepareRetirementArguments, MirrorPulseControlNamespaceMemberRetirementPreparation>(
            MirrorPulseControlCommands.NamespaceMemberPrepareRetirement, new(operationId, memberId, recursive), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementResult> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        ExecuteNamespaceMemberRetirementAsync(operationId, false, cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementResult> ExecuteNamespaceMemberRetirementAsync(Guid operationId,
        bool recursive, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberRetirementArguments, MirrorPulseControlNamespaceMemberRetirementResult>(
            MirrorPulseControlCommands.NamespaceMemberRetirement, new(operationId, recursive), cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementResult> GetNamespaceMemberRetirementAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        GetNamespaceMemberRetirementAsync(operationId, false, cancellationToken);

    public Task<MirrorPulseControlNamespaceMemberRetirementResult> GetNamespaceMemberRetirementAsync(Guid operationId,
        bool recursive, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceMemberRetirementStatusArguments, MirrorPulseControlNamespaceMemberRetirementResult>(
            MirrorPulseControlCommands.NamespaceMemberRetirementStatus, new(operationId, recursive), cancellationToken);

    public Task<MirrorPulseControlRemoteRetirementStatus> GetNamespaceRemoteRetirementStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceRemoteRetirementStatusArguments, MirrorPulseControlRemoteRetirementStatus>(
            MirrorPulseControlCommands.NamespaceRemoteRetirementStatus, new(operationId), cancellationToken);

    public Task<MirrorPulseControlRemoteRetirementPage> GetNamespaceRemoteRetirementsAsync(Guid rootId,
        Guid? afterOperationId = null, int limit = 64, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceRemoteRetirementsArguments, MirrorPulseControlRemoteRetirementPage>(
            MirrorPulseControlCommands.NamespaceRemoteRetirements, new(rootId, afterOperationId, limit), cancellationToken);

    public Task<MirrorPulseControlRemoteDeleteStatus> GetNamespaceRemoteDeleteStatusAsync(Guid commandId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceRemoteDeleteStatusArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteStatus, new(commandId), cancellationToken);

    public Task<MirrorPulseControlRemoteDeletePage> GetNamespaceRemoteDeletesAsync(Guid rootId, Guid? afterCommandId = null,
        int limit = 64, CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceRemoteDeletesArguments, MirrorPulseControlRemoteDeletePage>(
            MirrorPulseControlCommands.NamespaceRemoteDeletes, new(rootId, afterCommandId, limit), cancellationToken);

    public Task<MirrorPulseControlRemoteDeleteStatus> RecoverNamespaceRemoteDeleteAsync(Guid commandId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceRemoteDeleteRecoverArguments, MirrorPulseControlRemoteDeleteStatus>(
            MirrorPulseControlCommands.NamespaceRemoteDeleteRecover, new(commandId), cancellationToken);

    public Task<MirrorPulseControlNamespaceBirthRecoveryResult> ExecuteNamespaceBirthRecoveryAsync(Guid recoveryId,
        CancellationToken cancellationToken = default) =>
        SendAsync<NamespaceBirthRecoverArguments, MirrorPulseControlNamespaceBirthRecoveryResult>(
            MirrorPulseControlCommands.NamespaceBirthRecover, new(recoveryId), cancellationToken);

    public Task<MirrorPulseControlConflictList> GetConflictsAsync(
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ConflictListArguments, MirrorPulseControlConflictList>(
            MirrorPulseControlCommands.ConflictList, new(limit, cursor), cancellationToken);

    public Task<MirrorPulseControlOperation> GetOperationAsync(
        string operationId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationIdArguments, MirrorPulseControlOperation>(
            MirrorPulseControlCommands.OperationGet, new(operationId), cancellationToken);

    public Task<MirrorPulseControlOperation> WatchOperationAsync(
        string operationId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationIdArguments, MirrorPulseControlOperation>(
            MirrorPulseControlCommands.OperationWatch, new(operationId), cancellationToken);

    public Task<MirrorPulseControlOperation> CancelOperationAsync(
        string operationId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationIdArguments, MirrorPulseControlOperation>(
            MirrorPulseControlCommands.OperationCancel, new(operationId), cancellationToken);

    public Task<MirrorPulseControlSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<ControlEmptyArguments, MirrorPulseControlSettings>(
            MirrorPulseControlCommands.SettingsGet, new(), cancellationToken);

    public Task<MirrorPulseControlSettings> SetSettingsAsync(
        MirrorPulseSettingsUpdateArguments arguments,
        CancellationToken cancellationToken = default) =>
        SendAsync<MirrorPulseSettingsUpdateArguments, MirrorPulseControlSettings>(
            MirrorPulseControlCommands.SettingsSet, arguments, cancellationToken);

    public Task<MirrorPulseControlDiagnosticsResult> CollectDiagnosticsAsync(
        DiagnosticsArguments arguments,
        CancellationToken cancellationToken = default) =>
        SendAsync<DiagnosticsArguments, MirrorPulseControlDiagnosticsResult>(
            MirrorPulseControlCommands.DiagnosticsCollect, arguments, cancellationToken);

    public Task<MirrorPulseAppStatusResponse> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdapterInstallArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.AdapterInstall,
            new(packagePath), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> RemoveAdapterAsync(
        string adapterId,
        string? installId = null,
        bool purge = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdapterRemoveArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.AdapterRemove,
            new(adapterId, purge, installId), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> CreateInstanceAsync(
        InstanceCreateArguments arguments,
        CancellationToken cancellationToken = default) =>
        SendAsync<InstanceCreateArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.InstanceCreate, arguments, cancellationToken);

    public Task<MirrorPulseAppStatusResponse> SetInstanceEnabledAsync(
        string instanceId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SendAsync<InstanceEnableArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.InstanceEnable,
            new(instanceId, enabled), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> ConfigureInstanceAsync(
        InstanceConfigureArguments arguments,
        CancellationToken cancellationToken = default) =>
        SendAsync<InstanceConfigureArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.InstanceConfigure, arguments, cancellationToken);

    public Task<MirrorPulseAppStatusResponse> SelectInstallationAsync(
        string instanceId,
        string installId,
        CancellationToken cancellationToken = default) =>
        SendAsync<InstanceSelectVersionArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.InstanceSelectVersion,
            new(instanceId, installId), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> SnoozeConflictAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ConflictSnoozeArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.ConflictSnooze,
            new(conflictId), cancellationToken);

    public Task<MirrorPulseAppStatusResponse> ResolveConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        string? preservedPath = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ConflictResolveArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.ConflictResolve,
            new(conflictId, action, preservedPath), cancellationToken);

    private async Task<System.IO.Pipes.NamedPipeClientStream> ConnectAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await NamedPipeWorkerClient.ConnectAsync(
                _pipeName, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            if (_options.EnsureHostStartedAsync is null)
            {
                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.HostUnavailable,
                        "The MirrorPulse Host is not running.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Network,
                        retryable: true));
            }

            try
            {
                await _options.EnsureHostStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception startException) when (startException is not StackOverflowException and not OutOfMemoryException)
            {
                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.HostUnavailable,
                        "The MirrorPulse Host could not be started.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Network,
                        retryable: true));
            }
            try
            {
                return await NamedPipeWorkerClient.ConnectAsync(
                    _pipeName, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryException) when (retryException is IOException or TimeoutException)
            {
                throw new MirrorPulseControlException(
                    new ControlError(
                        MirrorPulseControlErrorCodes.HostUnavailable,
                        "The MirrorPulse Host could not be started.",
                        MirrorPulse.Core.Contracts.ErrorCategory.Network,
                        retryable: true));
            }
        }
    }
}
