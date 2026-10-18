namespace MirrorPulse.Cli;

public sealed record MirrorPulseCliGlobalOptions(
    bool Json = false,
    bool NoStart = false,
    TimeSpan? Timeout = null,
    bool DeveloperMode = false);

public sealed record MirrorPulseCliCommand(
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Arguments,
    MirrorPulseCliGlobalOptions Options)
{
    public string Name => Path.Count == 0 ? string.Empty : string.Join(' ', Path);
}

public sealed record MirrorPulseCliParseResult(
    MirrorPulseCliCommand? Command,
    bool ShowHelp,
    bool ShowVersion,
    string? Error)
{
    public bool Succeeded => Error is null;

    public MirrorPulseCliGlobalOptions Options { get; init; } = new();
}

public static class MirrorPulseCliCommandLine
{
    private static readonly HashSet<string> Commands = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "adapter", "config", "conflict", "developer-mode", "diagnostics", "help",
        "instance", "namespace", "operation", "startup", "status", "sync", "version", "host", "root"
    };

    private static readonly Dictionary<string, IReadOnlySet<string>> Subcommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["adapter"] = NewSet("list", "install", "remove", "update", "uninstall"),
            ["conflict"] = NewSet("list", "show", "snooze", "resolve"),
            ["host"] = NewSet("status", "start", "stop", "restart"),
            ["instance"] = NewSet("list", "create", "configure", "enable", "disable", "select-version"),
            ["operation"] = NewSet("get", "watch", "cancel"),
            ["root"] = NewSet("list", "prepare-rename", "recover"),
            ["namespace"] = NewSet("member", "birth", "protection", "remote"),
            ["sync"] = NewSet("status", "refresh")
        };

    public static MirrorPulseCliParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var options = new MirrorPulseCliGlobalOptions();
        var commandPath = new List<string>();
        var commandArguments = new List<string>();
        bool showHelp = false;
        bool showVersion = false;
        bool commandStarted = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string token = arguments[index];
            if (!commandStarted && token is "-h" or "--help")
            {
                showHelp = true;
                continue;
            }

            if (!commandStarted && token.Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                showVersion = true;
                continue;
            }

            if (!commandStarted && token.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { Json = true };
                continue;
            }

            if (!commandStarted && token.Equals("--no-start", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { NoStart = true };
                continue;
            }

            if (!commandStarted && token.Equals("--developer-mode", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { DeveloperMode = true };
                continue;
            }

            if (!commandStarted && token.Equals("--timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count)
                {
                    return Failure("Option --timeout requires a value in seconds.", options);
                }

                if (!double.TryParse(arguments[index], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds) ||
                    seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds))
                {
                    return Failure("Option --timeout requires a positive number of seconds.", options);
                }

                options = options with { Timeout = TimeSpan.FromSeconds(seconds) };
                continue;
            }

            if (!commandStarted && token.StartsWith('-'))
            {
                return Failure($"Unknown option '{token}'.", options);
            }

            if (!commandStarted)
            {
                commandStarted = true;
                commandPath.Add(token);
                continue;
            }

            if (commandPath.Count == 1 && Subcommands.TryGetValue(commandPath[0], out var subcommands))
            {
                if (!subcommands.Contains(token))
                {
                    return Failure($"Unknown subcommand '{token}' for '{commandPath[0]}'.", options);
                }

                commandPath.Add(token);
                continue;
            }

            if (commandPath.Count == 2 &&
                commandPath[0].Equals("namespace", StringComparison.OrdinalIgnoreCase) &&
                commandArguments.Count == 0)
            {
                bool allowed = commandPath[1].Equals("member", StringComparison.OrdinalIgnoreCase)
                    ? token.Equals("inspect", StringComparison.OrdinalIgnoreCase) || token.Equals("tree", StringComparison.OrdinalIgnoreCase) || token.Equals("prepare-move", StringComparison.OrdinalIgnoreCase) || token.Equals("move", StringComparison.OrdinalIgnoreCase) || token.Equals("move-status", StringComparison.OrdinalIgnoreCase) || token.Equals("create", StringComparison.OrdinalIgnoreCase) || token.Equals("prepare-import", StringComparison.OrdinalIgnoreCase) || token.Equals("import", StringComparison.OrdinalIgnoreCase) || token.Equals("import-status", StringComparison.OrdinalIgnoreCase) || token.Equals("prepare-replace", StringComparison.OrdinalIgnoreCase) || token.Equals("replace", StringComparison.OrdinalIgnoreCase) || token.Equals("replace-recover", StringComparison.OrdinalIgnoreCase) || token.Equals("replacement-status", StringComparison.OrdinalIgnoreCase) || token.Equals("prepare-retire", StringComparison.OrdinalIgnoreCase) || token.Equals("retire", StringComparison.OrdinalIgnoreCase) || token.Equals("retirement-status", StringComparison.OrdinalIgnoreCase)
                    : commandPath[1].Equals("protection", StringComparison.OrdinalIgnoreCase)
                        ? token.Equals("captures", StringComparison.OrdinalIgnoreCase) || token.Equals("recover-tree", StringComparison.OrdinalIgnoreCase) ||
                            token.Equals("initialize", StringComparison.OrdinalIgnoreCase) || token.Equals("recover-current", StringComparison.OrdinalIgnoreCase) ||
                            token.Equals("recover-root", StringComparison.OrdinalIgnoreCase) || token.Equals("initialization-status", StringComparison.OrdinalIgnoreCase) ||
                            token.Equals("initializations", StringComparison.OrdinalIgnoreCase)
                        : commandPath[1].Equals("remote", StringComparison.OrdinalIgnoreCase)
                            ? token.Equals("retirement-status", StringComparison.OrdinalIgnoreCase) || token.Equals("retirements", StringComparison.OrdinalIgnoreCase)
                            : token.Equals("recover", StringComparison.OrdinalIgnoreCase);
                if (!allowed)
                    return Failure($"Unknown subcommand '{token}' for 'namespace {commandPath[1]}'.", options);
                commandPath.Add(token);
                continue;
            }

            commandArguments.Add(token);
        }

        if (showHelp || showVersion)
        {
            return new(null, showHelp, showVersion, null) { Options = options };
        }

        if (commandPath.Count == 0)
        {
            return new(null, true, false, null) { Options = options };
        }

        if (!Commands.Contains(commandPath[0]))
        {
            return Failure($"Unknown command '{commandPath[0]}'.", options);
        }

        if (commandPath[0] is "help" or "version")
        {
            return new(new(commandPath, commandArguments, options),
                commandPath[0].Equals("help", StringComparison.OrdinalIgnoreCase),
                commandPath[0].Equals("version", StringComparison.OrdinalIgnoreCase), null)
            { Options = options };
        }

        return new(new(commandPath, commandArguments, options), false, false, null)
        { Options = options };
    }

    private static MirrorPulseCliParseResult Failure(
        string message,
        MirrorPulseCliGlobalOptions options) =>
        new(null, false, false, message) { Options = options };

    private static HashSet<string> NewSet(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

public static class MirrorPulseCliHelp
{
    public static string Text => """
        MirrorPulse command line

        Usage:
          mp [global options] <command> [subcommand] [arguments]

        Global options:
          -h, --help       Show help.
          --version        Show the CLI version.
          --json           Emit machine-readable output.
          --no-start       Do not start the Host automatically.
          --developer-mode Allow the configured development Host executable.
          --timeout SEC    Set the control request timeout.

        Commands:
          status                         Show sync status.
          host status|start|stop|restart Manage the current-user Host.
          sync status|refresh             Inspect or refresh synchronization.
          adapter list|install|update|remove|uninstall
                                         Manage installed Adapters.
          instance list|create|configure|enable|disable|select-version
                                         Manage Adapter instances.
          root list                      Inspect managed roots and namespace recovery.
          root prepare-rename ROOT_ID LABEL
                                         Prepare a Label-only managed root rename.
          root recover OPERATION_ID      Retry an original managed root rename proof.
          namespace member inspect RELATIVE_PATH [--directory]
                                         Look up a registered native member and its parent.
          namespace member tree MEMBER_ID
                                         Read the validated current member subtree.
          namespace member prepare-move MEMBER_ID PARENT_ID NAME [--operation-id GUID]
                                         Prepare a file move within its managed root.
          namespace member move OPERATION_ID
                                         Execute or retry an admitted protected member move.
          namespace member move-status OPERATION_ID
                                         Read the durable move progress.
          namespace member create PARENT_ID NAME [--directory] [--operation-id GUID]
          namespace member prepare-import PARENT_ID NAME SOURCE_PATH [--operation-id GUID]
          namespace member import OPERATION_ID
          namespace member import-status OPERATION_ID
          namespace member prepare-replace MEMBER_ID SOURCE_PATH [--operation-id GUID]
          namespace member replace OPERATION_ID
          namespace member replace-recover OPERATION_ID
          namespace member replacement-status OPERATION_ID
          namespace protection captures ROOT_ID [--after MANIFEST_ID]
                                         Read a bounded page of retained original captures.
          namespace protection recover-tree MANIFEST_ID
                                         Recover and audit one sealed original inventory.
          namespace protection initialize [--operation-id GUID]
          namespace protection recover-current [--operation-id GUID]
          namespace protection recover-root ROOT_ID [--operation-id GUID]
                                         Initialize the admitted original root inventories.
          namespace protection initialization-status OPERATION_ID
                                         Read initialization admission and completion.
          namespace protection initializations [--after OPERATION_ID]
                                         Find a bounded page of retained initialization attempts.
                                         Create an empty local file or directory under a protected member.
          namespace member prepare-retire MEMBER_ID [--recursive] [--operation-id GUID]
                                         Prepare a protected leaf or subtree deletion.
          namespace member retire OPERATION_ID [--recursive]
                                         Execute or retry an admitted protected deletion.
          namespace member retirement-status OPERATION_ID [--recursive]
                                         Read a protected deletion receipt.
          namespace remote retirement-status OPERATION_ID
                                         Read an original remote directory deletion status.
          namespace remote retirements ROOT_ID [--after-operation-id OPERATION_ID] [--limit 1..64]
                                         List original remote directory deletion statuses for one root.
          namespace birth recover RECOVERY_ID
                                         Retry an already retained staged-file recovery.
          conflict list|show|snooze|resolve
                                         Inspect and resolve conflicts.
          operation get|watch|cancel      Inspect long-running operations.
          config                         Read or update settings.
          developer-mode                 Read or update developer mode.
          startup                        Read or update startup behavior.
          diagnostics                    Create a local diagnostic package.
        """;
}
