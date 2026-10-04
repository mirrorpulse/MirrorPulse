using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Classifies current content state under the product's actual None registration policy.</summary>
public static class MirrorPulseJournalContentPolicy
{
    public static bool IsAcceptedObservation(CloudItemSnapshot snapshot, InstanceId instanceId, string? acknowledgedRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Exists || snapshot.Kind != CloudItemKind.File || !snapshot.IsPlaceholder ||
            snapshot.SynchronizationState != CloudSynchronizationState.InSync || snapshot.LocalBinding is null ||
            snapshot.PlaceholderIdentity.IsEmpty || string.IsNullOrEmpty(acknowledgedRevision)) return false;
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        CloudPlaceholderIdentity owned = MirrorPulsePlaceholderIdentity.Create(instanceId, identity.RemoteId, acknowledgedRevision).ToCfSharp();
        return identity.ItemId == owned.ItemId && identity.RemoteRevision == acknowledgedRevision &&
            snapshot.RemoteId == identity.RemoteId && snapshot.RemoteRevision == acknowledgedRevision;
    }
}
