using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal static class MirrorPulseConflictRemoteObservationProjection
{
    internal static async Task PublishAsync(MirrorPulseProductCatalog catalog, MirrorPulseCoalescedConflictRemoteObservation observed,
        MirrorPulseConflictCenter? center, MirrorPulseConflictNotificationBridge? notifications, CancellationToken token)
    {
        foreach (Guid previous in observed.PreviousConflictIds) center?.Remove(previous);
        foreach (Guid selected in observed.SelectedConflictIds)
        {
            var conflict = await catalog.ReadUploadConflictAsync(selected, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A committed remote version observation lost its selected epoch.");
            if (!conflict.IsPending) { center?.Remove(selected); continue; }
            center?.Upsert(conflict);
            if (notifications is not null)
            {
                try { await notifications.NotifyAsync(conflict, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        }
    }
}
