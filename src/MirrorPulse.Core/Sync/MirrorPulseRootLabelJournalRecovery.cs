using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Replays already owned original root notifications; it grants no native or root-work authority.</summary>
/// <remarks>The adapter binds the acknowledgement callback to the running public SDK feed under
/// the normal Host owner. Only its actual successful return permits durable settlement. This
/// boundary accepts no new observation, changes no revision and never infers a return from absence.</remarks>
public sealed class MirrorPulseRootLabelJournalRecovery(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceActor actor,
    Func<Guid, CancellationToken, Task> acknowledge, TimeProvider? timeProvider = null)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MirrorPulseNamespaceActor _actor = actor ?? throw new ArgumentNullException(nameof(actor));
    private readonly Func<Guid, CancellationToken, Task> _acknowledge = acknowledge ?? throw new ArgumentNullException(nameof(acknowledge));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public ValueTask<MirrorPulseRootLabelJournalAcknowledgement?> RecoverRootAsync(RootId rootId, CancellationToken token = default) =>
        _actor.RunAsync<MirrorPulseRootLabelJournalAcknowledgement?>(async stop =>
        {
            var original = await _catalog.ReadPendingRootLabelJournalAcknowledgementAsync(rootId, stop).ConfigureAwait(false);
            return original is null ? null : await AcknowledgeUnderActorAsync(original, stop).ConfigureAwait(false);
        }, token);

    /// <summary>Classifies an already owned original before nullable ItemId or ordinary Worker planning.</summary>
    public ValueTask<bool> TryReplayOwnedAsync(Guid operationId, CancellationToken token = default) =>
        _actor.RunAsync(async stop =>
        {
            var original = await _catalog.ReadRootLabelJournalAcknowledgementAsync(operationId, stop).ConfigureAwait(false);
            if (original is null) return false;
            _ = await AcknowledgeUnderActorAsync(original, stop).ConfigureAwait(false);
            return true;
        }, token);

    private async Task<MirrorPulseRootLabelJournalAcknowledgement> AcknowledgeUnderActorAsync(
        MirrorPulseRootLabelJournalAcknowledgement original, CancellationToken token)
    {
        _actor.AssertAccess();
        await _acknowledge(original.Observation.OperationId, token).ConfigureAwait(false);
        _actor.AssertAccess();
        // Cancellation or a lost catalog write cannot turn an uncertain SDK return into a
        // receipt. After an actual return, finish this immutable first start without cancellation.
        return await _catalog.CompleteRootLabelJournalAcknowledgementAsync(original with { AcknowledgedAt = null },
            _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    }
}
