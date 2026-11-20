using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Completes a local-only choice without requiring or accepting any remote transport.</summary>
public sealed class MirrorPulseConflictLocalRemovalExecutor(MirrorPulseProductCatalog catalog)
{
    public async ValueTask<MirrorPulseConflictLocalRemoval> ExecuteAsync(Guid commandId,
        Func<MirrorPulseConflictLocalRemoval, CancellationToken, ValueTask> completeLocal,
        Func<MirrorPulseConflictLocalRemovalIntent, CancellationToken, ValueTask> acknowledgeOriginals,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(completeLocal); ArgumentNullException.ThrowIfNull(acknowledgeOriginals);
        var decision = await catalog.ReadConflictLocalRemovalAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The local-only decision is missing.");
        if (decision.Phase == MirrorPulseConflictLocalRemovalPhase.Completed) return decision;
        await completeLocal(decision, token).ConfigureAwait(false);
        decision = await catalog.ReadConflictLocalRemovalAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The local-only decision disappeared after local completion.");
        if (decision.Phase != MirrorPulseConflictLocalRemovalPhase.Retired)
            throw new InvalidOperationException("Preservation and controlled local retirement must finish before original acknowledgement.");
        await acknowledgeOriginals(decision.Intent, token).ConfigureAwait(false);
        await catalog.AdvanceConflictLocalRemovalAsync(commandId, MirrorPulseConflictLocalRemovalPhase.Retired,
            MirrorPulseConflictLocalRemovalPhase.Completed, token).ConfigureAwait(false);
        return decision with { Phase = MirrorPulseConflictLocalRemovalPhase.Completed };
    }
}
