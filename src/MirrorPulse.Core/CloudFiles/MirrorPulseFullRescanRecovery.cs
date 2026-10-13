using MirrorPulse.Core.State;

namespace MirrorPulse.Core.CloudFiles;

/// <summary>Retains a scan generation across discovery, official acknowledgement and product projection gaps.</summary>
public sealed class MirrorPulseFullRescanRecovery(MirrorPulseProductCatalog catalog,
    Func<CancellationToken, ValueTask<int>> rescan, Func<CancellationToken, ValueTask> acknowledge,
    Func<Guid, CancellationToken, ValueTask> project)
{
    public async ValueTask<MirrorPulseFullRescanResult> RunAsync(MirrorPulseLocalChangeSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        MirrorPulseFullRescanCheckpoint? checkpoint = await catalog.ReadFullRescanAsync(cancellationToken).ConfigureAwait(false);
        if (!signal.RequiresFullRescan && checkpoint is null) return new(false, 0);
        checkpoint ??= await catalog.BeginFullRescanAsync(cancellationToken).ConfigureAwait(false);
        var coordinator = new MirrorPulseFullRescanCoordinator(async token =>
        {
            await catalog.UpdateFullRescanAsync(checkpoint.Generation, MirrorPulseFullRescanPhase.Running, token).ConfigureAwait(false);
            int count = await rescan(token).ConfigureAwait(false);
            await catalog.UpdateFullRescanAsync(checkpoint.Generation, MirrorPulseFullRescanPhase.Acknowledging, token).ConfigureAwait(false);
            return count;
        }, acknowledge);
        // Repeat discovery after restart, including a previous ack, because local files may
        // have changed. The mutation ledger prevents repeated remote side effects.
        MirrorPulseFullRescanResult result = await coordinator.HandleAsync(new(true), cancellationToken).ConfigureAwait(false);
        await project(checkpoint.Generation, cancellationToken).ConfigureAwait(false);
        await catalog.CompleteFullRescanAsync(checkpoint.Generation, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
