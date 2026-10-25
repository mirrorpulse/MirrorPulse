namespace MirrorPulse.Core.Sync;

/// <summary>Retries only a rolled-back new admission, allowing its original journal owner to progress.</summary>
/// <remarks>The action must release all native/actor/instance/admission leases before it throws.
/// The caller's original cancellation/deadline is retained. This never retries remote execution.</remarks>
public static class MirrorPulseTransferAdmissionRetry
{
    public static ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> prepare,
        CancellationToken cancellationToken = default) =>
        RunAsync(prepare, static (delay, token) => new(Task.Delay(delay, token)), cancellationToken);

    internal static async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> prepare,
        Func<TimeSpan, CancellationToken, ValueTask> wait, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(wait);
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await prepare(token).ConfigureAwait(false); }
            catch (MirrorPulseTransferAdmissionBusyException) when (attempt < 4)
            {
                // No action scope remains held while the original owner uses its
                // ordinary dispatch boundary. Never extend the request deadline.
                await wait(TimeSpan.FromMilliseconds(250 * (1 << attempt)), token).ConfigureAwait(false);
            }
        }
    }
}
