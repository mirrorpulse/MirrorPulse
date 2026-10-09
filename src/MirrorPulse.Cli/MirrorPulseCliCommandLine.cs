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
        "instance", "operation", "startup", "status", "sync", "version", "host", "root"
    };

    private static readonly Dictionary<string, IReadOnlySet<string>> Subcommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["adapter"] = NewSet("list", "install", "remove", "update", "uninstall"),
            ["conflict"] = NewSet("list", "show", "snooze", "resolve"),
            ["host"] = NewSet("status", "start", "stop", "restart"),
            ["instance"] = NewSet("list", "create", "configure", "enable", "disable", "select-version"),
            ["operation"] = NewSet("get", "watch", "cancel"),
            ["root"] = NewSet("list", "recover"),
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
          root recover OPERATION_ID      Retry an original managed root rename proof.
          conflict list|show|snooze|resolve
                                         Inspect and resolve conflicts.
          operation get|watch|cancel      Inspect long-running operations.
          config                         Read or update settings.
          developer-mode                 Read or update developer mode.
          startup                        Read or update startup behavior.
          diagnostics                    Create a local diagnostic package.
        """;
}
