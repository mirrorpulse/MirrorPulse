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
    private readonly MirrorPulseControlClientOptions _options;
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
