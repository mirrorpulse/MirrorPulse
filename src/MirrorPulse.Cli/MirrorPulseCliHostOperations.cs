using System.Diagnostics;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

public interface IMirrorPulseCliHostOperations
{
    Task EnsureStartedAsync(CancellationToken cancellationToken);

    Task<int> RunHostCommandAsync(
        string action,
        bool json,
        TextWriter output,
        TextWriter errorWriter,
        CancellationToken cancellationToken);
}

/// <summary>Executes a parsed non-lifecycle command through the Host control contract.</summary>
public interface IMirrorPulseCliCommandOperations
{
    Task<int> RunCommandAsync(
        MirrorPulseCliCommand command,
        bool json,
        TextWriter output,
        TextWriter errorWriter,
        CancellationToken cancellationToken);
}

public static class MirrorPulseCliHostPathResolver
{
    public static MirrorPulseHostStartupOptions CreateDefault(bool developerMode)
    {
        string siblingHost = Path.Combine(AppContext.BaseDirectory, "MirrorPulse.Host.exe");
        string packagedHost = Path.Combine(AppContext.BaseDirectory, "host", "MirrorPulse.Host.exe");
        string? explicitHost = Environment.GetEnvironmentVariable("MIRRORPULSE_HOST_PATH");
        bool developer = developerMode ||
            string.Equals(Environment.GetEnvironmentVariable("MIRRORPULSE_DEVELOPER_MODE"),
                "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("MIRRORPULSE_DEVELOPER_MODE"),
                "true", StringComparison.OrdinalIgnoreCase);

        return new MirrorPulseHostStartupOptions
        {
            InstalledHostPath = !developer && File.Exists(siblingHost)
                ? siblingHost
                : !developer && File.Exists(packagedHost) ? packagedHost : null,
            DevelopmentHostPath = explicitHost ?? siblingHost,
            DeveloperMode = developer
        };
    }
}

public sealed class MirrorPulseCliHostOperations : IMirrorPulseCliHostOperations,
    IMirrorPulseCliCommandOperations, IAsyncDisposable
{
    private readonly MirrorPulseHostStartupCoordinator _startup;
    private readonly MirrorPulseControlClient _client;

    public MirrorPulseCliHostOperations(MirrorPulseHostStartupOptions options, MirrorPulseControlClient? client = null)
    {
        _startup = new MirrorPulseHostStartupCoordinator(options: options);
        _client = client ?? new MirrorPulseControlClient(new()
        {
            GetExpectedHostExecutablePath = _startup.ResolveExecutablePath,
        });
    }

    public Task EnsureStartedAsync(CancellationToken cancellationToken) =>
        _startup.EnsureStartedAsync(cancellationToken);

    public async Task<int> RunHostCommandAsync(
        string action,
        bool json,
        TextWriter output,
        TextWriter errorWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            MirrorPulseHostStatus status = action.ToLowerInvariant() switch
            {
                "status" => await _client.GetHostStatusAsync(cancellationToken).ConfigureAwait(false),
                "start" => await StartAsync(cancellationToken).ConfigureAwait(false),
                "stop" => await _client.StopHostAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
                "restart" => await _client.RestartHostAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown Host action '{action}'.")
            };
            if (action.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                await WaitForHostExitAsync(status.ProcessId, cancellationToken).ConfigureAwait(false);
            }

            string human =
                $"{status.State} (pid {status.ProcessId}, pipe {status.ControlPipeName})";
            await MirrorPulseCliOutputFormatter.WriteDataAsync(
                status, human, json, output, cancellationToken).ConfigureAwait(false);
            return MirrorPulseControlExitCodes.Success;
        }
        catch (MirrorPulseControlException exception)
        {
            return await WriteControlErrorAsync(exception.Error, json, errorWriter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                MirrorPulseControlExitCodes.Validation, "mp.cli.validation", exception.Message,
                json, errorWriter, cancellationToken).ConfigureAwait(false);
            return MirrorPulseControlExitCodes.Validation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                MirrorPulseControlExitCodes.Cancelled,
                "mp.control.cancelled",
                "The operation was cancelled.",
                json,
                errorWriter,
                cancellationToken).ConfigureAwait(false);
            return MirrorPulseControlExitCodes.Cancelled;
        }
    }

    private static async Task WaitForHostExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MirrorPulseControlException(new ControlError(
                    MirrorPulseControlErrorCodes.RequestTimeout,
                    "The MirrorPulse Host did not exit after its lifecycle command.",
                    MirrorPulse.Core.Contracts.ErrorCategory.Native,
                    retryable: true));
            }
        }
    }

    public async ValueTask DisposeAsync() => await _startup.DisposeAsync().ConfigureAwait(false);

    public async Task<int> RunCommandAsync(
        MirrorPulseCliCommand command,
        bool json,
        TextWriter output,
        TextWriter errorWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            object result;
            string human;
            int exitCode = MirrorPulseControlExitCodes.Success;
            switch (command.Path[0].ToLowerInvariant(),
                command.Path.Count > 1 ? command.Path[1].ToLowerInvariant() : string.Empty)
            {
                case ("status", _):
                case ("sync", "status"):
                    result = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    human = FormatStatus((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result);
                    break;
                case ("sync", "refresh"):
                    result = await _client.RefreshAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    human = FormatStatus((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result);
                    break;
                case ("adapter", "list"):
                    result = await _client.GetAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
                    human = FormatTopology((MirrorPulseControlTopology)result);
                    break;
                case ("adapter", "install"):
                    string packagePath = GetRequiredValue(command.Arguments, "package", 0,
                        "adapter install requires a .mpadapter package path.");
                    result = await _client.InstallAsync(packagePath, cancellationToken).ConfigureAwait(false);
                    human = $"Adapter package installed: " +
                        ((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result).InstalledAdapterId;
                    break;
                case ("adapter", "update"):
                    packagePath = GetRequiredValue(command.Arguments, "package", 0,
                        "adapter update requires a .mpadapter package path.");
                    result = await _client.InstallAsync(packagePath, cancellationToken).ConfigureAwait(false);
                    human = "Adapter package updated: " +
                        ((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result).InstalledAdapterId;
                    break;
                case ("adapter", "remove"):
                case ("adapter", "uninstall"):
                    string adapterId = GetRequiredOption(command.Arguments, "adapter-id");
                    result = await _client.RemoveAdapterAsync(adapterId,
                        GetOptionalOption(command.Arguments, "install-id"),
                        HasFlag(command.Arguments, "purge"), cancellationToken).ConfigureAwait(false);
                    human = "Adapter installation removed: " + adapterId;
                    break;
                case ("instance", "list"):
                    result = await _client.GetInstancesAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    human = FormatTopology((MirrorPulseControlTopology)result);
                    break;
                case ("instance", "create"):
                    result = await _client.CreateInstanceAsync(new InstanceCreateArguments(
                        GetRequiredOption(command.Arguments, "install-id"),
                        GetRequiredOption(command.Arguments, "name"),
                        ParseMap(command.Arguments, "config"),
                        ParseMap(command.Arguments, "root"),
                        GetOptionalOption(command.Arguments, "secret"),
                        !HasFlag(command.Arguments, "disabled")), cancellationToken)
                        .ConfigureAwait(false);
                    human = "Adapter instance created: " +
                        ((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result).CreatedInstanceId;
                    break;
                case ("instance", "configure"):
                    string instanceId = GetRequiredOption(command.Arguments, "instance-id");
                    result = await _client.ConfigureInstanceAsync(new InstanceConfigureArguments(
                        instanceId,
                        GetRequiredOption(command.Arguments, "name"),
                        ParseMap(command.Arguments, "config"),
                        ParseMap(command.Arguments, "root")), cancellationToken)
                        .ConfigureAwait(false);
                    human = "Adapter instance configured: " + instanceId;
                    break;
                case ("instance", "enable"):
                case ("instance", "disable"):
                    instanceId = GetRequiredOption(command.Arguments, "instance-id");
                    bool enabled = command.Path[1].Equals("enable", StringComparison.OrdinalIgnoreCase);
                    result = await _client.SetInstanceEnabledAsync(instanceId, enabled, cancellationToken)
                        .ConfigureAwait(false);
                    human = $"Adapter instance {(enabled ? "enabled" : "disabled")}: {instanceId}";
                    break;
                case ("instance", "select-version"):
                    instanceId = GetRequiredOption(command.Arguments, "instance-id");
                    string installId = GetRequiredOption(command.Arguments, "install-id");
                    result = await _client.SelectInstallationAsync(instanceId, installId, cancellationToken)
                        .ConfigureAwait(false);
                    human = $"Adapter instance version selected: {installId}";
                    break;
                case ("conflict", "list"):
                    result = await _client.GetConflictsAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    human = $"Conflicts: {((MirrorPulseControlConflictList)result).Items.Count}";
                    break;
                case ("conflict", "show"):
                    string conflictId = GetRequiredOption(command.Arguments, "conflict-id");
                    MirrorPulseControlConflictList allConflicts = await _client.GetConflictsAsync(
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    MirrorPulseControlConflict? selected = allConflicts.Items.SingleOrDefault(item =>
                        item.ConflictId.Equals(conflictId, StringComparison.OrdinalIgnoreCase));
                    if (selected is null)
                        throw new FileNotFoundException("The conflict was not found.");
                    result = selected;
                    human = $"Conflict {selected.ConflictId}: {selected.RelativePath} ({selected.Source})";
                    break;
                case ("conflict", "snooze"):
                    conflictId = GetRequiredOption(command.Arguments, "conflict-id");
                    result = await _client.SnoozeConflictAsync(Guid.Parse(conflictId), cancellationToken)
                        .ConfigureAwait(false);
                    human = $"Conflict snoozed: {conflictId}";
                    break;
                case ("conflict", "resolve"):
                    conflictId = GetRequiredOption(command.Arguments, "conflict-id");
                    if (!Enum.TryParse(GetRequiredOption(command.Arguments, "action"), true,
                            out MirrorPulse.Core.Conflicts.MirrorPulseConflictAction action) ||
                        !Enum.IsDefined(action) || action == MirrorPulse.Core.Conflicts.MirrorPulseConflictAction.Defer)
                        throw new ArgumentException("The conflict action is invalid.");
                    result = await _client.ResolveConflictAsync(Guid.Parse(conflictId), action,
                        GetOptionalOption(command.Arguments, "preserved-path"), cancellationToken)
                        .ConfigureAwait(false);
                    MirrorPulse.Core.Conflicts.MirrorPulseConflictCommandResult? outcome =
                        ((MirrorPulse.Core.Host.MirrorPulseAppStatusResponse)result).ConflictCommand;
                    if (outcome is null)
                    {
                        human = $"Conflict action accepted; completion is unknown: {conflictId}";
                        exitCode = MirrorPulseControlExitCodes.Pending;
                    }
                    else
                    {
                        human = outcome.State == "resolved" ? $"Conflict resolved: {conflictId}" :
                            $"Conflict action {outcome.State}: {conflictId}; command {outcome.CommandId:D}";
                        exitCode = outcome.State == "resolved" ? MirrorPulseControlExitCodes.Success :
                            outcome.State == "pending" ? MirrorPulseControlExitCodes.Pending : MirrorPulseControlExitCodes.Conflict;
                    }
                    break;
                case ("operation", "get"):
                case ("operation", "watch"):
                    string operationId = GetRequiredOption(command.Arguments, "operation-id");
                    result = command.Path[1].Equals("watch", StringComparison.OrdinalIgnoreCase)
                        ? await _client.WatchOperationAsync(operationId, cancellationToken)
                            .ConfigureAwait(false)
                        : await _client.GetOperationAsync(operationId, cancellationToken)
                            .ConfigureAwait(false);
                    human = $"Operation {operationId}: {((MirrorPulseControlOperation)result).State}";
                    break;
                case ("operation", "cancel"):
                    operationId = GetRequiredOption(command.Arguments, "operation-id");
                    result = await _client.CancelOperationAsync(operationId, cancellationToken)
                        .ConfigureAwait(false);
                    human = $"Operation cancelled: {operationId}";
                    break;
                case ("config", _):
                    if (command.Arguments.Count == 0)
                    {
                        result = await _client.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                        human = FormatSettings((MirrorPulseControlSettings)result);
                    }
                    else
                    {
                        result = await _client.SetSettingsAsync(new MirrorPulseSettingsUpdateArguments(
                            GetOptionalOption(command.Arguments, "locale"),
                            ParseNullableBool(command.Arguments, "developer-mode"),
                            ParseNullableBool(command.Arguments, "start-with-windows"),
                            GetOptionValues(command.Arguments, "enable-installation"),
                            GetOptionalOption(command.Arguments, "sync-root-display-name")),
                            cancellationToken).ConfigureAwait(false);
                        human = FormatSettings((MirrorPulseControlSettings)result);
                    }
                    break;
                case ("developer-mode", _):
                    result = command.Arguments.Count == 0
                        ? await _client.GetSettingsAsync(cancellationToken).ConfigureAwait(false)
                        : await _client.SetSettingsAsync(new MirrorPulseSettingsUpdateArguments(
                            DeveloperMode: ParseRequiredBool(command.Arguments, "developer-mode")),
                            cancellationToken).ConfigureAwait(false);
                    human = $"Developer mode: {((MirrorPulseControlSettings)result).DeveloperMode}";
                    break;
                case ("startup", _):
                    result = command.Arguments.Count == 0
                        ? await _client.GetSettingsAsync(cancellationToken).ConfigureAwait(false)
                        : await _client.SetSettingsAsync(new MirrorPulseSettingsUpdateArguments(
                            StartWithWindows: ParseRequiredBool(command.Arguments, "start-with-windows")),
                            cancellationToken).ConfigureAwait(false);
                    human = $"Start with Windows: {((MirrorPulseControlSettings)result).StartWithWindows}";
                    break;
                case ("diagnostics", _):
                    result = await _client.CollectDiagnosticsAsync(new DiagnosticsArguments(
                        HasFlag(command.Arguments, "include-logs"),
                        GetOptionalOption(command.Arguments, "output")), cancellationToken)
                        .ConfigureAwait(false);
                    human = "Diagnostics package: " +
                        ((MirrorPulseControlDiagnosticsResult)result).PackagePath;
                    break;
                default:
                    await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                        MirrorPulseControlExitCodes.Unsupported,
                        "mp.control.unsupported",
                        $"Command '{command.Name}' is recognized but is not available in this milestone.",
                        json, errorWriter, cancellationToken).ConfigureAwait(false);
                    return MirrorPulseControlExitCodes.Unsupported;
            }

            await MirrorPulseCliOutputFormatter.WriteDataAsync(
                result, human, json, output, cancellationToken).ConfigureAwait(false);
            return exitCode;
        }
        catch (MirrorPulseControlException exception)
        {
            return await WriteControlErrorAsync(exception.Error, json, errorWriter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                MirrorPulseControlExitCodes.Cancelled, "mp.control.cancelled",
                "The operation was cancelled.", json, errorWriter, cancellationToken).ConfigureAwait(false);
            return MirrorPulseControlExitCodes.Cancelled;
        }
    }

    private static string FormatStatus(MirrorPulse.Core.Host.MirrorPulseAppStatusResponse status) =>
        $"Pending uploads: {status.PendingUploads}; remote conflicts: {status.PendingRemoteConflicts}; " +
        $"instances: {status.Instances.Count}";

    private static string FormatTopology(MirrorPulseControlTopology topology) =>
        $"Installations: {topology.Installations.Count}; instances: {topology.Instances.Count}; " +
        $"roots: {topology.Roots.Count}";

    private static string FormatSettings(MirrorPulseControlSettings settings) =>
        $"Locale: {settings.Locale}; developer mode: {settings.DeveloperMode}; " +
        $"start with Windows: {settings.StartWithWindows}";

    private static string GetRequiredValue(
        IReadOnlyList<string> arguments,
        string option,
        int positionalIndex,
        string error)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--" + option, StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
                throw new ArgumentException(error);
            return arguments[index + 1];
        }

        string[] positional = arguments.Where(item => !item.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        if (positionalIndex < positional.Length && !string.IsNullOrWhiteSpace(positional[positionalIndex]))
            return positional[positionalIndex];
        throw new ArgumentException(error);
    }

    private static string GetRequiredOption(IReadOnlyList<string> arguments, string name) =>
        GetOptionalOption(arguments, name) ??
        throw new ArgumentException($"The --{name} option is required.");

    private static string? GetOptionalOption(IReadOnlyList<string> arguments, string name)
    {
        string prefix = "--" + name + "=";
        for (int index = 0; index < arguments.Count; index++)
        {
            string value = arguments[index];
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..];
            if (value.Equals("--" + name, StringComparison.OrdinalIgnoreCase))
                return index + 1 < arguments.Count ? arguments[index + 1] : null;
        }

        return null;
    }

    private static bool HasFlag(IReadOnlyList<string> arguments, string name) =>
        arguments.Any(value => value.Equals("--" + name, StringComparison.OrdinalIgnoreCase));

    private static bool? ParseNullableBool(IReadOnlyList<string> arguments, string name)
    {
        string? value = GetOptionalOption(arguments, name);
        return value is null ? null : ParseBool(value, name);
    }

    private static bool ParseRequiredBool(IReadOnlyList<string> arguments, string name) =>
        ParseBool(GetRequiredOption(arguments, name), name);

    private static bool ParseBool(string value, string name) =>
        bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException($"The --{name} value must be true or false.");

    private static List<string>? GetOptionValues(
        IReadOnlyList<string> arguments, string name)
    {
        var values = new List<string>();
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--" + name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                throw new ArgumentException($"The --{name} option requires a value.");
            values.Add(arguments[index]);
        }

        return values.Count == 0 ? null : values;
    }

    private static Dictionary<string, string> ParseMap(
        IReadOnlyList<string> arguments, string option)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--" + option, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++index >= arguments.Count)
                throw new ArgumentException($"The --{option} option requires key=value.");
            string item = arguments[index];
            int separator = item.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException($"The --{option} option requires key=value.");
            map[item[..separator]] = item[(separator + 1)..];
        }

        return map;
    }

    private async Task<MirrorPulseHostStatus> StartAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await _client.StartHostAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> WriteControlErrorAsync(
        ControlError error,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        int exitCode = MirrorPulseCliExitCodeMapper.From(error);
        await MirrorPulseCliOutputFormatter.WriteErrorAsync(
            exitCode,
            error.Code,
            error.Message,
            json,
            output,
            cancellationToken).ConfigureAwait(false);
        return exitCode;
    }
}

public static class MirrorPulseCliExitCodeMapper
{
    public static int From(ControlError error) => error.Category switch
    {
        MirrorPulse.Core.Contracts.ErrorCategory.Validation => MirrorPulseControlExitCodes.Validation,
        MirrorPulse.Core.Contracts.ErrorCategory.Authentication => MirrorPulseControlExitCodes.Authentication,
        MirrorPulse.Core.Contracts.ErrorCategory.Authorization => MirrorPulseControlExitCodes.Authorization,
        MirrorPulse.Core.Contracts.ErrorCategory.Network => MirrorPulseControlExitCodes.Unavailable,
        MirrorPulse.Core.Contracts.ErrorCategory.Conflict => MirrorPulseControlExitCodes.Conflict,
        MirrorPulse.Core.Contracts.ErrorCategory.Unsupported => MirrorPulseControlExitCodes.Unsupported,
        MirrorPulse.Core.Contracts.ErrorCategory.Storage => MirrorPulseControlExitCodes.Storage,
        MirrorPulse.Core.Contracts.ErrorCategory.Cancelled => MirrorPulseControlExitCodes.Cancelled,
        _ when error.Code == MirrorPulseControlErrorCodes.RequestTimeout ||
                error.Code == MirrorPulseControlErrorCodes.HostStartTimeout =>
            MirrorPulseControlExitCodes.Timeout,
        _ => MirrorPulseControlExitCodes.Internal
    };
}

public static class MirrorPulseCliHostOperationsError
{
    public static async Task<int> WriteAsync(
        ControlError error,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        int exitCode = MirrorPulseCliExitCodeMapper.From(error);
        await MirrorPulseCliOutputFormatter.WriteErrorAsync(
            exitCode,
            error.Code,
            error.Message,
            json,
            output,
            cancellationToken).ConfigureAwait(false);
        return exitCode;
    }
}
