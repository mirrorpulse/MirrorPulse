using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Classifies current content state under the product's actual None registration policy.</summary>
public static class MirrorPulseJournalContentPolicy
{
    public static bool IsAcceptedObservation(CloudItemSnapshot snapshot, InstanceId instanceId, string? acknowledgedRevision)
        => IsAcceptedObservationCore(snapshot, identity => MirrorPulsePlaceholderIdentity.BelongsToInstance(instanceId, identity), acknowledgedRevision, CloudItemKind.File);

    public static bool IsAcceptedObservation(CloudItemSnapshot snapshot, RootRegistration root, string? acknowledgedRevision)
        => IsAcceptedObservationCore(snapshot, identity => MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity), acknowledgedRevision, CloudItemKind.File);

    public static bool IsAcceptedDirectoryObservation(CloudItemSnapshot snapshot, RootRegistration root, string remoteId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Exists || snapshot.Kind != CloudItemKind.Directory || !snapshot.IsPlaceholder ||
            snapshot.SynchronizationState != CloudSynchronizationState.InSync || snapshot.PlaceholderIdentity.IsEmpty) return false;
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        // A directory revision can change with children or timestamps. This
        // recognizes the previously accepted namespace object, not metadata sync.
        CloudPlaceholderIdentity expected = MirrorPulsePlaceholderIdentity.CreateForRoot(root, remoteId, identity.RemoteRevision).ToCfSharp();
        return MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity) && identity.ItemId == expected.ItemId &&
            identity.RemoteId == expected.RemoteId && snapshot.RemoteId == identity.RemoteId;
    }

    private static bool IsAcceptedObservationCore(CloudItemSnapshot snapshot, Func<CloudPlaceholderIdentity, bool> owns, string? acknowledgedRevision, CloudItemKind kind)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Exists || snapshot.Kind != kind || !snapshot.IsPlaceholder ||
            snapshot.SynchronizationState != CloudSynchronizationState.InSync || snapshot.LocalBinding is null ||
            snapshot.PlaceholderIdentity.IsEmpty || string.IsNullOrEmpty(acknowledgedRevision)) return false;
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        return owns(identity) && identity.RemoteRevision == acknowledgedRevision &&
            snapshot.RemoteId == identity.RemoteId && snapshot.RemoteRevision == acknowledgedRevision;
    }
}
