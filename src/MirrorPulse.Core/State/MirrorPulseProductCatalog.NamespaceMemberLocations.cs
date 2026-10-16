using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The product's committed namespace position, not a fresh native observation.</summary>
/// <remarks>Original membership and ACL provenance remain immutable. Pending native movement does not advance this position.</remarks>
public sealed record MirrorPulseNamespaceMemberLocation(Guid MemberId, Guid? ParentMemberId, string RelativePath,
    Guid? CompletedMoveOperationId)
{
    /// <summary>The completed operation that last changed this position, including an ancestor move.</summary>
    public Guid? PositionOperationId { get; init; }

    public Guid? CurrentPositionOperationId => PositionOperationId ?? CompletedMoveOperationId;
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceMemberLocation> ReadNamespaceMemberLocationAsync(Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (await ReadCompletedNamespaceRetirementForMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false))
                throw new FileNotFoundException("The namespace member has been retired.");
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return location;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberLocation> ReadNamespaceMemberLocationCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        var direct = await ReadNamespaceMemberDirectMoveLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        var position = await ReadLatestNamespaceMemberPositionCoreAsync(member, transaction, token).ConfigureAwait(false);
        if (position is null) return direct;
        if (position.CompletedMoveOperationId != direct.CompletedMoveOperationId)
            throw new InvalidDataException("The current position has lost the member's completed direct relocation head.");
        return new(member.MemberId, position.ParentMemberId, position.RelativePath, position.CompletedMoveOperationId)
        { PositionOperationId = position.OperationId == position.CompletedMoveOperationId ? null : position.OperationId };
    }

    private async Task<MirrorPulseNamespaceMemberLocation> ReadNamespaceMemberDirectMoveLocationCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? head = null;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT c.operation_id FROM namespace_member_move_completions c WHERE c.member_id=$member
                AND NOT EXISTS (
                    SELECT 1 FROM namespace_member_moves successor
                    JOIN namespace_member_move_completions finished ON finished.operation_id=successor.operation_id
                    WHERE successor.previous_move_id=c.operation_id)
                LIMIT 2;
                """;
            query.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) head = ParseNamespaceMemberId(reader.GetString(0));
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidDataException("The current member position has more than one completed relocation head.");
        }
        await using SqliteCommand count = _connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM namespace_member_move_completions WHERE member_id=$member;";
        count.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
        long total = (long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        if (head is null)
        {
            if (total != 0) throw new InvalidDataException("The member's completed relocation history has no head.");
            return new(member.MemberId, member.ParentMemberId, member.RelativePath, null);
        }
        var intent = await ReadNamespaceMemberMoveCoreAsync(head.Value, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current relocation head lost its intent.");
        long length = await ValidateNamespaceMemberMoveChainAsync(intent, transaction, token).ConfigureAwait(false);
        var completion = await ReadNamespaceMemberMoveCompletionCoreAsync(head.Value, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current relocation head lost its completion.");
        if (intent.MemberId != member.MemberId || total != length)
            throw new InvalidDataException("The member's completed relocations do not form one continuous history.");
        return new(member.MemberId, intent.Target.ParentMemberId, completion.RelativePath, head);
    }

    // Iterate instead of recursively reading predecessors. Each retained fact checks its own
    // intent/start/protection references; this loop independently verifies the entire ancestry.
    private async Task<long> ValidateNamespaceMemberMoveChainAsync(MirrorPulseNamespaceMemberMoveIntent head,
        SqliteTransaction transaction, CancellationToken token)
    {
        var visited = new HashSet<Guid> { head.OperationId };
        var current = head;
        long length = 1;
        while (current.PreviousMoveOperationId is { } previousId)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(previousId)) throw new InvalidDataException("The relocation history contains a cycle.");
            var previous = await ReadNamespaceMemberMoveCoreAsync(previousId, transaction, token, false).ConfigureAwait(false)
                ?? throw new InvalidDataException("The relocation predecessor lost its intent.");
            _ = await ReadNamespaceMemberMoveCompletionCoreAsync(previousId, transaction, token, false).ConfigureAwait(false)
                ?? throw new InvalidDataException("The relocation predecessor has no verified completion.");
            current = previous;
            length++;
        }
        return length;
    }

    private async Task RequireNamespaceMemberMoveSourceAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.SourcePositionOperationId is { } positionId && positionId != intent.PreviousMoveOperationId)
        {
            var source = await ReadNamespaceMemberPositionEvidenceCoreAsync(member, positionId, intent.PreparedAt, transaction, token).ConfigureAwait(false);
            if (source.ParentMemberId != intent.Source.ParentMemberId || source.RelativePath != intent.Source.RelativePath ||
                source.CompletedMoveOperationId != intent.PreviousMoveOperationId)
                throw new InvalidOperationException("The source must follow the same member's completed position and direct move history.");
            if (intent.PreviousMoveOperationId is { } retainedMoveId)
            {
                var retainedMove = await ReadNamespaceMemberMoveCoreAsync(retainedMoveId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The source position lost its direct move predecessor.");
                var retainedCompletion = await ReadNamespaceMemberMoveCompletionCoreAsync(retainedMoveId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The source position lost its completed direct relocation.");
                if (retainedMove.MemberId != member.MemberId || retainedMove.LocalObject != member.LocalObject ||
                    retainedMove.RootId != member.RootId || retainedCompletion.VerifiedAt > source.VerifiedAt)
                    throw new InvalidOperationException("The source placement must retain the member's earlier direct relocation.");
                await RequireNamespaceMoveIdentitySuccessorAsync(retainedMove, intent, retainedCompletion.VerifiedAt, transaction, token).ConfigureAwait(false);
            }
            return;
        }
        if (intent.PreviousMoveOperationId is not { } previousId)
        {
            if (intent.Source.ParentMemberId != member.ParentMemberId || intent.Source.RelativePath != member.RelativePath)
                throw new InvalidOperationException("A first relocation must start at the original registered position.");
            return;
        }
        var previous = await ReadNamespaceMemberMoveRowAsync(previousId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The relocation predecessor is missing.");
        var row = await ReadNamespaceMemberMoveCompletionRowAsync(previousId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The relocation predecessor is incomplete.");
        var completion = row.Completion;
        if (previous.MemberId != member.MemberId || row.MemberId != member.MemberId || previous.RootId != intent.RootId ||
            previous.LocalObject != intent.LocalObject ||
            previous.OwnerSid != intent.OwnerSid || previous.Target.ParentMemberId != intent.Source.ParentMemberId ||
            previous.Target.ParentLocalObject != intent.Source.ParentLocalObject || previous.Target.RelativePath != intent.Source.RelativePath ||
            completion.RelativePath != intent.Source.RelativePath || completion.ItemId != previous.ItemId || completion.RemoteId != previous.RemoteId ||
            completion.IsDirectory != member.IsDirectory || completion.VerifiedAt > intent.PreparedAt)
            throw new InvalidOperationException("A relocation must follow the same object's completed destination and chronology.");
        await RequireNamespaceMoveIdentitySuccessorAsync(previous, intent, completion.VerifiedAt, transaction, token).ConfigureAwait(false);
    }

    private static async Task EnsureNamespaceMemberMoveChainSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM pragma_table_info('namespace_member_moves') WHERE name='previous_move_id';";
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
        {
            command.CommandText = "ALTER TABLE namespace_member_moves ADD COLUMN previous_move_id TEXT NULL REFERENCES namespace_member_move_completions(operation_id);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        // NULL preserves existing first-relocation records. Migration invents no predecessor,
        // native observation, accepted source identity, or completion for a legacy operation.
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_namespace_move_completion_member ON namespace_member_move_completions(member_id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
