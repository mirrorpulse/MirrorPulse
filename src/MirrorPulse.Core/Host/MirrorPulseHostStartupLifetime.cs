namespace MirrorPulse.Core.Host;

/// <summary>Publishes the control listener before initialization and drains both owners on failure or shutdown.</summary>
public static class MirrorPulseHostStartupLifetime
{
    public static async Task RunAsync(Func<CancellationToken, Task> serve, Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serve); ArgumentNullException.ThrowIfNull(initialize);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? serving = null, initializing = null; Exception? failure = null;
        try
        {
            shutdown.Token.ThrowIfCancellationRequested();
            serving = serve(shutdown.Token);
            if (serving.IsCompleted)
            {
                await serving.ConfigureAwait(false);
                shutdown.Token.ThrowIfCancellationRequested();
                throw new IOException("The Host control listener ended before initialization.");
            }
            initializing = initialize(shutdown.Token);
            Task first = await Task.WhenAny(serving, initializing).ConfigureAwait(false);
            if (first == serving)
            {
                await serving.ConfigureAwait(false);
                shutdown.Token.ThrowIfCancellationRequested();
                throw new IOException("The Host control listener ended during initialization.");
            }
            await initializing.ConfigureAwait(false);
            await serving.ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        await MirrorPulseHostResourceCleanup.RunAsync([
            () => new(shutdown.CancelAsync()),
            () => DrainAsync(initializing),
            () => DrainAsync(serving)
        ], failure).ConfigureAwait(false);

        async ValueTask DrainAsync(Task? owned)
        {
            if (owned is null) return;
            try { await owned.ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception error) when (ReferenceEquals(error, failure)) { }
        }
    }
}
