using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>The result of one bounded metadata attempt; an error never supplies an ACK receipt.</summary>
public sealed record MirrorPulseRootLabelJournalRecoveryAttempt(Guid OperationId, Exception? Failure);

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
    private Guid? _after;
    private bool _recoveryScanInProgress;
    private bool _rotationHadFailure;

    /// <summary>Prevents an empty feed wait from suspending an unfinished metadata recovery rotation.</summary>
    public bool RecoveryScanInProgress => Volatile.Read(ref _recoveryScanInProgress);

    /// <summary>Rotates a bounded page of pending originals, including disabled roots, before feed planning.</summary>
    public ValueTask<IReadOnlyList<MirrorPulseRootLabelJournalRecoveryAttempt>> RecoverPendingAsync(
        int limit = 4, CancellationToken token = default)
    {
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        return _actor.RunAsync<IReadOnlyList<MirrorPulseRootLabelJournalRecoveryAttempt>>(async stop =>
        {
            var ids = await _catalog.ReadPendingRootLabelJournalOperationIdsAsync(_after, limit, stop).ConfigureAwait(false);
            if (ids.Count == 0 && _after is not null)
            {
                _after = null;
                _rotationHadFailure = false;
                ids = await _catalog.ReadPendingRootLabelJournalOperationIdsAsync(null, limit, stop).ConfigureAwait(false);
            }
            Volatile.Write(ref _recoveryScanInProgress, ids.Count != 0);
            var attempts = new List<MirrorPulseRootLabelJournalRecoveryAttempt>(ids.Count);
            foreach (Guid id in ids)
            {
                stop.ThrowIfCancellationRequested();
                // Advance before the individual point read or return, including failures.
                // The durable original and its fence remain unchanged until actual success.
                _after = id;
                try
                {
                    var original = await _catalog.ReadRootLabelJournalAcknowledgementAsync(id, stop).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The pending root ACK lost its immutable original.");
                    _ = await AcknowledgeUnderActorAsync(original, stop).ConfigureAwait(false);
                    attempts.Add(new(id, null));
                }
                catch (Exception failure) when (!stop.IsCancellationRequested && failure is
                    (IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SqliteException or OperationCanceledException))
                {
                    _rotationHadFailure = true;
                    attempts.Add(new(id, failure));
                }
            }
            // A successful partial last page cannot erase failures on earlier pages.
            bool continueRecovery = ids.Count == limit || _rotationHadFailure;
            if (ids.Count < limit) { _after = null; _rotationHadFailure = false; }
            Volatile.Write(ref _recoveryScanInProgress, continueRecovery);
            return attempts.AsReadOnly();
        }, token);
    }

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
