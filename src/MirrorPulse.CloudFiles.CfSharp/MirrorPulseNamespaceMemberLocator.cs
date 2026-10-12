using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public enum MirrorPulseNamespaceMemberLocationState
{
    SyncRootAnchor,
    IdentityUnavailable,
    ProjectionMissing,
    Projected,
    ProjectedTombstone
}

/// <summary>A current official item projection, separately from immutable member provenance.</summary>
/// <remarks>
/// A projection is a location candidate, not a fresh native binding or permission check.
/// Tombstones do not retire membership or release an owned name. A changed RemoteId requires
/// separate acceptance evidence before mutation authorization. CfSharp owns item paths,
/// revisions, move recovery and its store encodings; this value does not copy that graph.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberLocationCandidate(MirrorPulseNamespaceMemberRegistration Member,
    MirrorPulseNamespaceMemberLocationState State, Guid? ItemId, string? OriginalRemoteId,
    string? RelativePath, string? RemoteId, string? RemoteRevision);

/// <summary>Locates a member through its retained public ItemId and CfSharp's official item repository.</summary>
public static class MirrorPulseNamespaceMemberLocator
{
    public static async Task<MirrorPulseNamespaceMemberLocationCandidate> ResolveAsync(MirrorPulseProductCatalog catalog,
        ICloudStateStore store, Guid memberId, MirrorPulseNamespaceActor namespaceActor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(namespaceActor);
        cancellationToken.ThrowIfCancellationRequested();
        namespaceActor.AssertAccess();
        var member = await catalog.ReadNamespaceMemberAsync(memberId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The namespace member is not registered.");
        if (member.ParentMemberId is null)
            return Candidate(MirrorPulseNamespaceMemberLocationState.SyncRootAnchor, null, null, null);

        var identity = await catalog.ReadNamespaceMemberIdentityAsync(memberId, cancellationToken).ConfigureAwait(false);
        Guid? itemId = identity.ItemId;
        string? originalRemoteId = identity.OriginalRemoteId;
        if (itemId is null)
            return Candidate(MirrorPulseNamespaceMemberLocationState.IdentityUnavailable, null, null, null);

        // Never adopt an item found at the first path or by a reused remote identifier.
        // The official volume-specific Int64 LocalFileId is not the full native binding.
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState? row = await transaction.Items.GetByItemIdAsync(itemId.Value, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            if (row.ItemId != itemId || row.Kind != (member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File))
                throw new InvalidDataException("The official item projection disagrees with the retained public member identity or kind.");
            if (!row.IsTombstone)
            {
                CloudItemState? atPath = await transaction.Items.GetByRelativePathAsync(row.RelativePath, cancellationToken).ConfigureAwait(false);
                CloudItemState? atRemote = await transaction.Items.GetByRemoteIdAsync(row.RemoteId, cancellationToken).ConfigureAwait(false);
                if (!SameProjection(row, atPath) || !SameProjection(row, atRemote))
                    throw new InvalidDataException("The current official item indexes disagree.");
            }
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        namespaceActor.AssertAccess();
        return Candidate(row is null ? MirrorPulseNamespaceMemberLocationState.ProjectionMissing : row.IsTombstone
            ? MirrorPulseNamespaceMemberLocationState.ProjectedTombstone : MirrorPulseNamespaceMemberLocationState.Projected,
            itemId, originalRemoteId, row);

        MirrorPulseNamespaceMemberLocationCandidate Candidate(MirrorPulseNamespaceMemberLocationState state,
            Guid? retainedItem, string? retainedRemote, CloudItemState? current)
        {
            namespaceActor.AssertAccess();
            return new(member, state, retainedItem, retainedRemote, current?.RelativePath, current?.RemoteId, current?.RemoteRevision);
        }
    }

    private static bool SameProjection(CloudItemState expected, CloudItemState? other) => other is not null &&
        expected.ItemId == other.ItemId && expected.RemoteId == other.RemoteId && expected.RelativePath == other.RelativePath &&
        expected.Kind == other.Kind && expected.RemoteRevision == other.RemoteRevision && expected.LocalFileId == other.LocalFileId &&
        expected.IsTombstone == other.IsTombstone && expected.UpdatedAt == other.UpdatedAt;
}
