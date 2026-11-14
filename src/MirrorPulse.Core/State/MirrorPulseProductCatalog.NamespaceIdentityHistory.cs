using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record NamespaceIdentityTransition(Guid OperationId, Guid? PreviousOperationId,
        Guid PreviousItemId, string PreviousRemoteId, Guid ItemId, string RemoteId, DateTimeOffset ObservedAt);

    private async Task<NamespaceIdentityTransition?> ReadNamespaceIdentityTransitionAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var moved = await ReadJournalDirectoryMoveIdentityTransitionAsync(member, operationId, transaction, token).ConfigureAwait(false);
        if (member.IsDirectory)
        {
            var proof = await ReadDirectoryAcceptanceCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (proof is null) return moved;
            if (moved is not null) throw new InvalidDataException("A Move projection cannot share a directory confirmation ID.");
            var confirmation = await ReadDirectoryConfirmationCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (confirmation is null) return null;
            if (proof.MemberId != member.MemberId || proof.RootId != member.RootId || proof.LocalObject != member.LocalObject)
                throw new InvalidOperationException("The directory confirmation belongs to a different registered object.");
            return new(operationId, proof.PreviousAcceptanceOperationId, proof.PreviousItemId, proof.PreviousRemoteId,
                proof.AcceptedItemId, proof.RemoteId, confirmation.ObservedAt);
        }
        var accepted = await ReadNamespaceIdentityAcceptanceRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (accepted is null) return moved;
        if (moved is not null) throw new InvalidDataException("A Move projection cannot share a file confirmation ID.");
        if (accepted.MemberId != member.MemberId || accepted.RootId != member.RootId ||
            accepted.Proof.UploadBinding.LocalObject != member.LocalObject)
            throw new InvalidOperationException("The upload confirmation belongs to a different registered object.");
        await ValidateNamespaceIdentityAcceptanceReferencesAsync(accepted, member, transaction, token).ConfigureAwait(false);
        return new(operationId, accepted.PreviousAcceptanceOperationId, accepted.PreviousItemId, accepted.PreviousRemoteId,
            accepted.Proof.AcceptedItemId, accepted.Proof.RemoteId, accepted.Confirmation.ObservedAt);
    }

    private async Task<(NamespaceIdentityTransition Head, int Count)> ValidateNamespaceIdentityHistoryAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid head, (Guid? ItemId, string? RemoteId) original,
        Guid itemId, string remoteId, DateTimeOffset latest, SqliteTransaction transaction, CancellationToken token,
        IReadOnlyDictionary<Guid, NamespaceIdentityTransition>? retainedMoves = null)
    {
        var visited = new HashSet<Guid>();
        NamespaceIdentityTransition? newest = null;
        Guid? cursor = head;
        while (cursor is { } operationId)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(operationId)) throw new InvalidDataException("The member identity history contains a cycle.");
            var transition = retainedMoves?.GetValueOrDefault(operationId) ??
                await ReadNamespaceIdentityTransitionAsync(member, operationId, transaction, token).ConfigureAwait(false) ??
                throw new FileNotFoundException("The member's retained identity transition is missing.");
            if (transition.ItemId != itemId || transition.RemoteId != remoteId ||
                transition.ObservedAt < member.RegisteredAt || transition.ObservedAt > latest)
                throw new InvalidOperationException("The identity history must follow the same registered object and observation order.");
            newest ??= transition;
            itemId = transition.PreviousItemId;
            remoteId = transition.PreviousRemoteId;
            latest = transition.ObservedAt;
            cursor = transition.PreviousOperationId;
        }
        if (original.ItemId != itemId || original.RemoteId != remoteId)
            throw new InvalidOperationException("The member identity history does not reach its registered identity.");
        return (newest!, visited.Count);
    }

    private async Task<MirrorPulseNamespaceMemberIdentity> ReadCurrentNamespaceIdentityHistoryAsync(
        MirrorPulseNamespaceMemberRegistration member, (Guid? ItemId, string? RemoteId) original,
        SqliteTransaction transaction, CancellationToken token)
    {
        var transitions = new List<(Guid Operation, Guid? Previous, bool Move)>();
        // Include successors indexed under another member. The ordinary predecessor is
        // globally unique; a cross-member reference is damaged history, not a valid older head.
        string ordinary = member.IsDirectory ? """
            SELECT operation_id,previous_operation_id,previous_move_id,0,member_id FROM directory_acceptances
            WHERE receipt IS NOT NULL AND (member_id=$member OR previous_operation_id IN
                (SELECT operation_id FROM directory_acceptances WHERE member_id=$member AND receipt IS NOT NULL))
            """ : """
            SELECT operation_id,previous_operation_id,previous_move_id,0,member_id FROM namespace_member_identity_acceptances
            WHERE member_id=$member OR previous_operation_id IN
                (SELECT operation_id FROM namespace_member_identity_acceptances WHERE member_id=$member)
            """;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = ordinary + """

                UNION ALL SELECT operation_id,NULL,NULL,1,member_id FROM journal_directory_move_projections WHERE member_id=$member;
                """;
            query.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetString(4) != member.MemberId.ToString("D"))
                    throw new InvalidDataException("An identity confirmation references another member's predecessor.");
                string? previous = ReadNamespaceIdentityPredecessor(reader, 1, 2);
                transitions.Add((ParseNamespaceMemberId(reader.GetString(0)),
                    previous is null ? null : ParseNamespaceMemberId(previous), reader.GetInt32(3) != 0));
            }
        }
        if (transitions.Count == 0)
            return new(member.MemberId, original.ItemId, original.RemoteId, null, original.ItemId, original.RemoteId);

        var predecessors = new HashSet<Guid>();
        var operationIds = new HashSet<Guid>();
        var moves = new Dictionary<Guid, NamespaceIdentityTransition>();
        foreach (var transition in transitions)
        {
            Guid? previous = transition.Previous;
            if (!operationIds.Add(transition.Operation))
                throw new InvalidDataException("The member identity history contains a duplicate operation.");
            if (transition.Move)
            {
                var projection = await ReadJournalDirectoryMoveIdentityTransitionAsync(member, transition.Operation, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A member identity projection disappeared.");
                moves.Add(transition.Operation, projection);
                previous = projection.PreviousOperationId;
            }
            if (previous is { } id && !predecessors.Add(id))
                throw new InvalidDataException("The member identity history forks at a confirmed predecessor.");
        }
        var heads = transitions.Where(transition => !predecessors.Contains(transition.Operation)).Take(2).ToArray();
        if (heads.Length != 1)
            throw new InvalidDataException("The member identity history must have exactly one current transition.");
        try
        {
            var head = moves.GetValueOrDefault(heads[0].Operation) ??
                await ReadNamespaceIdentityTransitionAsync(member, heads[0].Operation, transaction, token).ConfigureAwait(false) ??
                throw new InvalidDataException("The member identity head disappeared.");
            var validated = await ValidateNamespaceIdentityHistoryAsync(member, head.OperationId, original, head.ItemId,
                head.RemoteId, DateTimeOffset.MaxValue, transaction, token, moves).ConfigureAwait(false);
            if (validated.Count != transitions.Count)
                throw new InvalidDataException("The member identity transitions do not form one continuous history.");
            return new(member.MemberId, head.ItemId, head.RemoteId, head.OperationId, original.ItemId, original.RemoteId);
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The member identity history lost its confirmed predecessor.", error);
        }
    }
}
