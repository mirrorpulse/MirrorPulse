using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPulse.Core;

namespace MirrorPulse.Cli;

public static class MirrorPulseCliOutputFormatter
{
    public const string SchemaVersion = "1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteVersionAsync(
        string version,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (json)
        {
            await WriteJsonAsync(output, new
            {
                SchemaVersion,
                Kind = "version",
                Version = version
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"{ProductInfo.Name} {MirrorPulseCliApplication.CommandName} {version}").ConfigureAwait(false);
    }

    public static async Task WriteHelpAsync(
        string help,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (json)
        {
            await WriteJsonAsync(output, new
            {
                SchemaVersion,
                Kind = "help",
                Text = help
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(help).ConfigureAwait(false);
    }

    public static async Task WriteErrorAsync(
        int exitCode,
        string code,
        string message,
        bool json,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (json)
        {
            await WriteJsonAsync(error, new
            {
                SchemaVersion,
                Kind = "error",
                ExitCode = exitCode,
                Code = code,
                Message = message
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await error.WriteLineAsync(message).ConfigureAwait(false);
    }

    public static async Task WriteDataAsync<T>(
        T value,
        string humanText,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (json)
        {
            await WriteJsonAsync(output, new
            {
                SchemaVersion,
                Kind = "result",
                Data = value
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(humanText).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        TextWriter writer,
        T value,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(value, JsonOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }
}
