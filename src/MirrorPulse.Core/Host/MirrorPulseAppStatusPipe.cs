using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Host;

public sealed record MirrorPulseAppInstanceStatus(
    string InstanceId,
    string DisplayName,
    bool Enabled,
    string Phase,
    string? CursorFingerprint,
    DateTimeOffset? CursorUpdatedAt,
    DateTimeOffset? LastSuccessfulSync,
    string? LastErrorCode = null,
    MirrorPulseTransferProgress? TransferProgress = null)
{
    public bool? WorkerConnected { get; init; }
}

public sealed record MirrorPulseAppNotification(
    string ConflictId,
    string RelativePath,
    DateTimeOffset DetectedAt,
    bool Snoozed,
    MirrorPulseConflictSource Source = MirrorPulseConflictSource.CfSharpRemote);

public sealed record MirrorPulseAppStatusResponse(
    int PendingUploads,
    int PendingRemoteConflicts,
    IReadOnlyList<MirrorPulseAppInstanceStatus> Instances,
    IReadOnlyList<MirrorPulseAppNotification> Notifications,
    string? Error = null,
    string? InstalledAdapterId = null,
    string? CreatedInstanceId = null,
    int PendingUploadConflicts = 0,
    IReadOnlyList<MirrorPulseBlockedLocalOperation>? BlockedLocalOperations = null,
    MirrorPulseConflictCommandResult? ConflictCommand = null);

public sealed record MirrorPulseCreateInstanceRequest(
    string InstallId,
    string DisplayName,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyDictionary<string, string> RootLabels,
    string? Secret,
    bool Enabled);

/// <summary>Current-user, bounded request/response channel from WinUI to the owner Host.</summary>
public sealed class MirrorPulseAppStatusPipe
{
    private const int MaximumFrameBytes = 1024 * 1024;
    private readonly Func<CancellationToken, Task<MirrorPulseAppStatusResponse>> _readStatus;
    private readonly Func<Guid, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _snooze;
    private readonly Func<Guid, MirrorPulseConflictAction, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _resolveConflict;
    private readonly Func<InstanceId, bool, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _setEnabled;
    private readonly Func<InstanceId, InstallId, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _selectVersion;
    private readonly Func<string, CancellationToken, Task<MirrorPulseAppStatusResponse>>? _install;
    private readonly Func<MirrorPulseCreateInstanceRequest, CancellationToken,
        Task<MirrorPulseAppStatusResponse>>? _createInstance;

    public MirrorPulseAppStatusPipe(
        Func<CancellationToken, Task<MirrorPulseAppStatusResponse>> readStatus,
        Func<Guid, CancellationToken, Task<MirrorPulseAppStatusResponse>>? snooze = null,
        Func<InstanceId, bool, CancellationToken, Task<MirrorPulseAppStatusResponse>>? setEnabled = null,
        Func<InstanceId, InstallId, CancellationToken, Task<MirrorPulseAppStatusResponse>>? selectVersion = null,
        Func<string, CancellationToken, Task<MirrorPulseAppStatusResponse>>? install = null,
        Func<MirrorPulseCreateInstanceRequest, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? createInstance = null,
        Func<Guid, MirrorPulseConflictAction, CancellationToken,
            Task<MirrorPulseAppStatusResponse>>? resolveConflict = null)
    {
        _readStatus = readStatus ?? throw new ArgumentNullException(nameof(readStatus));
        _snooze = snooze;
        _resolveConflict = resolveConflict;
        _setEnabled = setEnabled;
        _selectVersion = selectVersion;
        _install = install;
        _createInstance = createInstance;
    }

    public static string CurrentUserPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        return $"MirrorPulse-app-{sid}";
    }

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = SecureNamedPipeServerFactory.Create(
                new NamedPipeServerOptions(CurrentUserPipeName()));
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                string request = JsonSerializer.Deserialize<string>(
                    await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false))
                    ?? string.Empty;
                MirrorPulseAppStatusResponse response;
                if (request == "status")
                {
                    try
                    {
                        response = await _readStatus(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("snooze:", StringComparison.Ordinal) &&
                    Guid.TryParseExact(request[7..], "D", out Guid conflictId) && _snooze is not null)
                {
                    try
                    {
                        response = await _snooze(conflictId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("conflict:", StringComparison.Ordinal) &&
                    _resolveConflict is not null && TryParseConflict(request,
                        out Guid resolveId, out MirrorPulseConflictAction resolveAction))
                {
                    try
                    {
                        response = await _resolveConflict(resolveId, resolveAction, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("enable:", StringComparison.Ordinal) && _setEnabled is not null &&
                    TryParseEnable(request, out InstanceId enableInstance, out bool enabled))
                {
                    try
                    {
                        response = await _setEnabled(enableInstance, enabled, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("version:", StringComparison.Ordinal) && _selectVersion is not null &&
                    TryParseVersion(request, out InstanceId versionInstance, out InstallId installId))
                {
                    try
                    {
                        response = await _selectVersion(versionInstance, installId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("install:", StringComparison.Ordinal) && _install is not null &&
                    request.Length > "install:".Length)
                {
                    try
                    {
                        response = await _install(request["install:".Length..], cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else if (request.StartsWith("create:", StringComparison.Ordinal) && _createInstance is not null)
                {
                    try
                    {
                        MirrorPulseCreateInstanceRequest create = JsonSerializer.Deserialize<MirrorPulseCreateInstanceRequest>(
                            request["create:".Length..]) ?? throw new InvalidDataException(
                                "The instance configuration is empty.");
                        response = await _createInstance(create, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = new(0, 0, [], [], exception.Message);
                    }
                }
                else
                {
                    response = new(0, 0, [], [], "Unknown Host request.");
                }

                await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // One disconnected UI client cannot stop the Host status channel.
            }
            catch (JsonException)
            {
                // A malformed request from one client cannot stop the Host status channel.
            }
            catch (InvalidDataException)
            {
                // An oversized request from one client cannot stop the Host status channel.
            }
        }
    }

    public static async Task<MirrorPulseAppStatusResponse> RequestAsync(
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync("status", cancellationToken).ConfigureAwait(false);

    public static async Task<MirrorPulseAppStatusResponse> SnoozeAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict ID cannot be empty.", nameof(conflictId));
        }

        return await SendRequestAsync($"snooze:{conflictId:D}", cancellationToken).ConfigureAwait(false);
    }

    public static Task<MirrorPulseAppStatusResponse> ResolveConflictAsync(
        Guid conflictId,
        MirrorPulseConflictAction action,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty || action is MirrorPulseConflictAction.Defer)
        {
            throw new ArgumentException("The conflict action is invalid.");
        }

        return SendRequestAsync($"conflict:{conflictId:D}:{action}", cancellationToken);
    }

    public static Task<MirrorPulseAppStatusResponse> SetInstanceEnabledAsync(
        InstanceId instanceId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync($"enable:{instanceId}:{(enabled ? "1" : "0")}", cancellationToken);

    public static Task<MirrorPulseAppStatusResponse> SelectInstallationAsync(
        InstanceId instanceId,
        InstallId installId,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync($"version:{instanceId}:{installId}", cancellationToken);

    public static Task<MirrorPulseAppStatusResponse> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        return SendRequestAsync($"install:{Path.GetFullPath(packagePath)}",
            TimeSpan.FromMinutes(2), cancellationToken);
    }

    public static Task<MirrorPulseAppStatusResponse> CreateInstanceAsync(
        MirrorPulseCreateInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequestAsync("create:" + JsonSerializer.Serialize(request),
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static bool TryParseEnable(string request, out InstanceId instanceId, out bool enabled)
    {
        string[] parts = request.Split(':');
        instanceId = default;
        enabled = parts.Length == 3 && parts[2] == "1";
        return parts.Length == 3 && (parts[2] is "0" or "1") &&
            InstanceId.TryParse(parts[1], out instanceId);
    }

    private static bool TryParseConflict(
        string request,
        out Guid conflictId,
        out MirrorPulseConflictAction action)
    {
        string[] parts = request.Split(':', 3);
        conflictId = default;
        action = default;
        return parts.Length == 3 && Guid.TryParseExact(parts[1], "D", out conflictId) &&
            Enum.TryParse(parts[2], ignoreCase: true, out action) &&
            action is not MirrorPulseConflictAction.Defer;
    }

    private static bool TryParseVersion(string request, out InstanceId instanceId, out InstallId installId)
    {
        string[] parts = request.Split(':');
        if (parts.Length == 3 && InstanceId.TryParse(parts[1], out instanceId) &&
            InstallId.TryParse(parts[2], out installId))
        {
            return true;
        }

        instanceId = default;
        installId = default;
        return false;
    }

    private static Task<MirrorPulseAppStatusResponse> SendRequestAsync(
        string request,
        CancellationToken cancellationToken) =>
        SendRequestAsync(request, TimeSpan.FromSeconds(5), cancellationToken);

    private static async Task<MirrorPulseAppStatusResponse> SendRequestAsync(
        string request,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        await using NamedPipeClientStream pipe = await NamedPipeWorkerClient.ConnectAsync(
            CurrentUserPipeName(), TimeSpan.FromSeconds(5), timeout.Token).ConfigureAwait(false);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), timeout.Token)
            .ConfigureAwait(false);
        byte[] payload = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
        MirrorPulseAppStatusResponse response = JsonSerializer.Deserialize<MirrorPulseAppStatusResponse>(payload)
            ?? throw new InvalidDataException("The Host returned an empty status response.");
        if (response.Error is not null)
        {
            throw new InvalidOperationException(response.Error);
        }

        return response;
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("The Host status frame is too large or empty.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("The Host status frame is too large or empty.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
