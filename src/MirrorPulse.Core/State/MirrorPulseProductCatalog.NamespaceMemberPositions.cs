using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>An immutable committed position fact for one member, independent of its first membership.</summary>
/// <remarks>
/// The affecting operation can move the member itself or an ancestor. CompletedMoveOperationId
/// remains this member's direct relocation head. A legacy anchor names an already completed
/// relocation; migration never invents a position fact or upgrades native authorization.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberPosition(int Version, Guid OperationId, Guid MemberId,
    Guid? PreviousPositionOperationId, Guid? ParentMemberId, string RelativePath,
    Guid? CompletedMoveOperationId, DateTimeOffset VerifiedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LegacyAnchor = false);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads a retained member position at one completed operation, without observing native state.</summary>
    public async Task<MirrorPulseNamespaceMemberPosition?> ReadNamespaceMemberPositionAsync(Guid memberId,
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("Member and completed position IDs are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var result = await ReadNamespaceMemberPositionRowAsync(memberId, operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                _ = await ValidateNamespaceMemberPositionHistoryAsync(member, result, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberPosition?> ReadLatestNamespaceMemberPositionCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        Guid? head = null;
        long total;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT p.operation_id FROM namespace_member_positions p WHERE p.member_id=$member
                AND NOT EXISTS(SELECT 1 FROM namespace_member_positions successor
                    WHERE successor.member_id=p.member_id AND successor.previous_operation_id=p.operation_id)
                LIMIT 2;
                """;
            query.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) head = ParseNamespaceMemberId(reader.GetString(0));
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidDataException("The current member position has multiple settlement heads.");
        }
        await using (SqliteCommand count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM namespace_member_positions WHERE member_id=$member;";
            count.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
            total = (long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }
        if (head is null)
        {
            if (total != 0) throw new InvalidDataException("The member position history has no head.");
            return null;
        }
        var position = await ReadNamespaceMemberPositionRowAsync(member.MemberId, head.Value, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current position head disappeared.");
        long length = await ValidateNamespaceMemberPositionHistoryAsync(member, position, transaction, token).ConfigureAwait(false);
        if (length != total) throw new InvalidDataException("The member position facts do not form one continuous history.");
        return position;
    }

    private async Task<long> ValidateNamespaceMemberPositionHistoryAsync(MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberPosition head, SqliteTransaction transaction, CancellationToken token)
    {
        var current = head;
        var visited = new HashSet<Guid>();
        long length = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(current.OperationId)) throw new InvalidDataException("The member position history contains a cycle.");
            await ValidateNamespaceMemberPositionReferencesAsync(member, current, transaction, token).ConfigureAwait(false);
            length++;
            if (current.PreviousPositionOperationId is not { } previousId) return length;
            var previous = await ReadNamespaceMemberPositionRowAsync(member.MemberId, previousId, transaction, token).ConfigureAwait(false);
            if (current.LegacyAnchor)
            {
                if (previous is not null) throw new InvalidDataException("A legacy position anchor cannot replace a retained settlement fact.");
                var legacy = await ReadNamespaceMemberMoveCoreAsync(previousId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The legacy position anchor lost its original relocation.");
                var completed = await ReadNamespaceMemberMoveCompletionCoreAsync(previousId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The legacy position anchor is incomplete.");
                if (legacy.MemberId != member.MemberId || legacy.LocalObject != member.LocalObject ||
                    completed.VerifiedAt >= current.VerifiedAt)
                    throw new InvalidDataException("The legacy position anchor must precede the same object's new settlement.");
                return length;
            }
            if (previous is null || previous.VerifiedAt >= current.VerifiedAt)
                throw new InvalidDataException("A position successor requires its original earlier settlement fact.");
            current = previous;
        }
    }

    private async Task ValidateNamespaceMemberPositionReferencesAsync(MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceMemberPosition position, SqliteTransaction transaction, CancellationToken token)
    {
        var move = await ReadNamespaceMemberMoveRowAsync(position.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The position lost its affecting move intent.");
        var row = await ReadNamespaceMemberMoveCompletionRowAsync(position.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The position lost its completed affecting move.");
        if (member.MemberId != position.MemberId || member.RegisteredAt >= position.VerifiedAt ||
            move.MemberId != member.MemberId || row.MemberId != member.MemberId || move.LocalObject != member.LocalObject ||
            move.OwnerSid != member.OwnerSid || move.RootId != member.RootId || position.CompletedMoveOperationId != move.OperationId ||
            move.Target.ParentMemberId != position.ParentMemberId || row.Completion.RelativePath != position.RelativePath ||
            row.Completion.VerifiedAt != position.VerifiedAt || move.PreparedAt >= position.VerifiedAt ||
            position.PreviousPositionOperationId != (move.SourcePositionOperationId ?? move.PreviousMoveOperationId))
            throw new InvalidDataException("The position must follow this member's exact completed relocation.");
        // The position loop verifies every predecessor once. Validate this operation's
        // references without recursively walking the same relocation ancestry again.
        _ = await ReadNamespaceMemberMoveCoreAsync(move.OperationId, transaction, token, validateRelocationChain: false).ConfigureAwait(false);
        _ = await ReadNamespaceMemberMoveCompletionCoreAsync(move.OperationId, transaction, token, validateRelocationChain: false).ConfigureAwait(false);
    }

    private async Task<(Guid? OperationId, bool LegacyAnchor)> PrepareNamespaceMemberPositionSuccessorAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        var position = await ReadLatestNamespaceMemberPositionCoreAsync(member, transaction, token).ConfigureAwait(false);
        if (position is not null) return (position.OperationId, false);
        var legacy = await ReadNamespaceMemberDirectMoveLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        return (legacy.CompletedMoveOperationId, legacy.CompletedMoveOperationId is not null);
    }

    private async Task InsertNamespaceMemberPositionCoreAsync(MirrorPulseNamespaceMemberPosition position,
        SqliteTransaction transaction, CancellationToken token)
    {
        ValidateNamespaceMemberPosition(position);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(position, TopologyJsonOptions);
        if (payload.Length > 131_072) throw new InvalidOperationException("The member position fact is too large.");
        await using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO namespace_member_positions(member_id,operation_id,previous_operation_id,payload,fingerprint)
            VALUES($member,$id,$previous,$payload,$fingerprint);
            """;
        insert.Parameters.AddWithValue("$member", position.MemberId.ToString("D"));
        insert.Parameters.AddWithValue("$id", position.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$previous", (object?)position.PreviousPositionOperationId?.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceMemberPosition?> ReadNamespaceMemberPositionRowAsync(Guid memberId,
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT member_id,operation_id,previous_operation_id,payload,fingerprint FROM namespace_member_positions WHERE member_id=$member AND operation_id=$id;";
        query.Parameters.AddWithValue("$member", memberId.ToString("D"));
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var position = ReadNamespaceMovePayload<MirrorPulseNamespaceMemberPosition>((byte[])reader[3], (byte[])reader[4],
            131_072, ValidateNamespaceMemberPosition);
        if (position.MemberId != memberId || position.OperationId != operationId || reader.GetString(0) != memberId.ToString("D") ||
            reader.GetString(1) != operationId.ToString("D") ||
            (reader.IsDBNull(2) ? null : reader.GetString(2)) != position.PreviousPositionOperationId?.ToString("D"))
            throw new InvalidDataException("The member position indexes disagree with the retained fact.");
        return position;
    }

    private static void ValidateNamespaceMemberPosition(MirrorPulseNamespaceMemberPosition position)
    {
        ValidatePermissionRelativePath(position.RelativePath);
        if (position.Version != 1 || position.MemberId == Guid.Empty || position.OperationId == Guid.Empty ||
            position.ParentMemberId is null || position.ParentMemberId == Guid.Empty || position.ParentMemberId == position.MemberId ||
            position.CompletedMoveOperationId == Guid.Empty || position.PreviousPositionOperationId == Guid.Empty ||
            position.PreviousPositionOperationId == position.OperationId || position.VerifiedAt == default ||
            position.LegacyAnchor && position.PreviousPositionOperationId is null)
            throw new ArgumentException("A member position requires complete identities and a distinct chronological predecessor.");
    }
}
