using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Classifies current content state under the product's actual None registration policy.</summary>
public static class MirrorPulseJournalContentPolicy
{
    public static bool IsAcceptedObservation(CloudItemSnapshot snapshot, InstanceId instanceId, string? acknowledgedRevision)
        => IsAcceptedObservationCore(snapshot, identity => MirrorPulsePlaceholderIdentity.BelongsToInstance(instanceId, identity), acknowledgedRevision);

    public static bool IsAcceptedObservation(CloudItemSnapshot snapshot, RootRegistration root, string? acknowledgedRevision)
        => IsAcceptedObservationCore(snapshot, identity => MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity), acknowledgedRevision);

    private static bool IsAcceptedObservationCore(CloudItemSnapshot snapshot, Func<CloudPlaceholderIdentity, bool> owns, string? acknowledgedRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Exists || snapshot.Kind != CloudItemKind.File || !snapshot.IsPlaceholder ||
            snapshot.SynchronizationState != CloudSynchronizationState.InSync || snapshot.LocalBinding is null ||
            snapshot.PlaceholderIdentity.IsEmpty || string.IsNullOrEmpty(acknowledgedRevision)) return false;
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        return owns(identity) && identity.RemoteRevision == acknowledgedRevision &&
            snapshot.RemoteId == identity.RemoteId && snapshot.RemoteRevision == acknowledgedRevision;
    }
}
