using System.Reflection;
using MirrorPulse.Control.Client;

namespace MirrorPulse.Cli;

/// <summary>
/// Minimal command-line entry point shared by the development executable and MSIX alias.
/// </summary>
public sealed class MirrorPulseCliApplication
{
    public static string ProductVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    internal static string CommandName => typeof(MirrorPulseCliApplication).Assembly.GetName().Name ?? "mp";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter? output = null,
        TextWriter? error = null,
        IMirrorPulseCliHostOperations? hostOperations = null,
        Func<bool>? isPlatformSupported = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        output ??= Console.Out;
        error ??= Console.Error;

        MirrorPulseCliParseResult parsed = MirrorPulseCliCommandLine.Parse(arguments);
        if (!parsed.Succeeded)
        {
            await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                1,
                "mp.cli.usage",
                parsed.Error!,
                parsed.Options.Json,
                error,
                cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (parsed.ShowHelp)
        {
            await MirrorPulseCliOutputFormatter.WriteHelpAsync(
                MirrorPulseCliHelp.Text, parsed.Options.Json, output, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        if (parsed.ShowVersion)
        {
            await MirrorPulseCliOutputFormatter.WriteVersionAsync(
                ProductVersion, parsed.Options.Json, output, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        if (parsed.Command is { Name: "help" or "version" })
        {
            if (parsed.Command.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                await MirrorPulseCliOutputFormatter.WriteVersionAsync(
                    ProductVersion, parsed.Options.Json, output, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await MirrorPulseCliOutputFormatter.WriteHelpAsync(
                    MirrorPulseCliHelp.Text, parsed.Options.Json, output, cancellationToken)
                    .ConfigureAwait(false);
            }

            return 0;
        }

        if (!(isPlatformSupported ?? MirrorPulseControlPlatform.IsCurrentSupported)())
        {
            await MirrorPulseCliOutputFormatter.WriteErrorAsync(
                8, "mp.platform.unsupported", MirrorPulseControlPlatform.Requirement,
                parsed.Options.Json, error, cancellationToken).ConfigureAwait(false);
            return 8;
        }

        bool isHostCommand = parsed.Command!.Path.Count >= 2 &&
            parsed.Command.Path[0].Equals("host", StringComparison.OrdinalIgnoreCase);
        hostOperations ??= new MirrorPulseCliHostOperations(
            MirrorPulseCliHostPathResolver.CreateDefault(parsed.Options.DeveloperMode));
        if (isHostCommand)
        {
            return await hostOperations.RunHostCommandAsync(
                parsed.Command.Path[1],
                parsed.Options.Json,
                output,
                error,
                cancellationToken).ConfigureAwait(false);
        }

        if (!parsed.Options.NoStart)
        {
            try
            {
                await hostOperations.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MirrorPulseControlException exception)
            {
                return await MirrorPulseCliHostOperationsError.WriteAsync(
                    exception.Error,
                    parsed.Options.Json,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (hostOperations is IMirrorPulseCliCommandOperations commandOperations)
        {
            return await commandOperations.RunCommandAsync(
                parsed.Command, parsed.Options.Json, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        string message = $"Command '{parsed.Command!.Name}' is recognized but is not available in this milestone.";
        await MirrorPulseCliOutputFormatter.WriteErrorAsync(
            8,
            "mp.control.unsupported",
            message,
            parsed.Options.Json,
            error,
            cancellationToken).ConfigureAwait(false);
        return 8;
    }
}
