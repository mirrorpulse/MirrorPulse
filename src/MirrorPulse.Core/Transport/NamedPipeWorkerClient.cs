using System.IO.Pipes;
using System.Security.Principal;

namespace MirrorPulse.Core.Transport;

/// <summary>
/// Opens a current-user Worker connection to a Named Pipe server.
/// </summary>
public static class NamedPipeWorkerClient
{
    public static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        var timeoutMilliseconds = ToTimeoutMilliseconds(timeout);
        var client = new NamedPipeClientStream(
            ".",
            pipeName.Trim(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);

        try
        {
            await client.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            NamedPipePeerIdentity.ValidateServer(client);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.Infinite;
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The connection timeout cannot be negative.");
        }

        var milliseconds = timeout.TotalMilliseconds;
        if (milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The connection timeout is too large.");
        }

        return (int)Math.Ceiling(milliseconds);
    }
}
