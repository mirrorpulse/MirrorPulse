using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task RequireNamespaceMoveIdentityAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceMemberRegistration member, bool current, SqliteTransaction transaction, CancellationToken token)
    {
        var original = await ReadOriginalNamespaceMemberIdentityAsync(member, transaction, token).ConfigureAwait(false);
        Guid? itemId = original.ItemId; string? remoteId = original.RemoteId;
        if (intent.IdentityAcceptanceOperationId is { } operationId)
        {
            var identity = await ValidateNamespaceIdentityHistoryAsync(member, operationId, original, intent.ItemId,
                intent.RemoteId, intent.PreparedAt, transaction, token).ConfigureAwait(false);
            itemId = identity.ItemId; remoteId = identity.RemoteId;
        }
        if (!member.IsDirectory && itemId is null)
            throw new InvalidOperationException("An original file move requires its retained pre-conversion identity.");
        if (itemId is not null && (intent.ItemId != itemId || intent.RemoteId != remoteId))
            throw new InvalidOperationException("A move cannot substitute an unproved public identity.");
        if (current)
        {
            var latest = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (latest.AcceptanceOperationId != intent.IdentityAcceptanceOperationId ||
                latest.ItemId is not null && (latest.ItemId != intent.ItemId || latest.RemoteId != intent.RemoteId))
                throw new InvalidOperationException("A new move must reference the member's latest confirmed identity.");
        }
    }

    private async Task RequireNamespaceMoveIdentitySuccessorAsync(MirrorPulseNamespaceMemberMoveIntent previous,
        MirrorPulseNamespaceMemberMoveIntent next, DateTimeOffset completedAt, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? cursor = next.IdentityAcceptanceOperationId;
        Guid itemId = next.ItemId; string remoteId = next.RemoteId;
        var visited = new HashSet<Guid>();
        var member = await ReadNamespaceMemberCoreAsync(next.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The relocated member is missing.");
        // The next intent already verified its entire acceptance ancestry. Here, additionally
        // prove that every new transition happened after the previous completed relocation.
        while (cursor != previous.IdentityAcceptanceOperationId)
        {
            token.ThrowIfCancellationRequested();
            if (cursor is not { } operationId || !visited.Add(operationId))
                throw new InvalidOperationException("A relocation cannot rewind or fork its accepted identity history.");
            var accepted = await ReadNamespaceIdentityTransitionAsync(member, operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The relocation lost an accepted identity successor.");
            if (accepted.ItemId != itemId || accepted.RemoteId != remoteId || accepted.ObservedAt < completedAt)
                throw new InvalidOperationException("An identity successor must follow the same object's completed relocation.");
            itemId = accepted.PreviousItemId; remoteId = accepted.PreviousRemoteId;
            cursor = accepted.PreviousOperationId;
        }
        if (itemId != previous.ItemId || remoteId != previous.RemoteId)
            throw new InvalidOperationException("The relocation's identity history does not reach its predecessor.");
    }
}
