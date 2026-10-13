using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static void AddNamespaceMemberMoveParameters(SqliteCommand command, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        command.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
        command.Parameters.AddWithValue("$root", intent.RootId.ToString());
        command.Parameters.AddWithValue("$source", intent.Source.ParentMemberId.ToString("D"));
        command.Parameters.AddWithValue("$target", intent.Target.ParentMemberId.ToString("D"));
        command.Parameters.AddWithValue("$name", NamespaceMemberNameKey(intent.Target.RelativePath));
        command.Parameters.AddWithValue("$previous", (object?)intent.PreviousMoveOperationId?.ToString("D") ?? DBNull.Value);
    }

    private async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadNamespaceMemberMoveCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token, bool validateRelocationChain = true)
    {
        token.ThrowIfCancellationRequested();
        var reads = _namespaceHistoryReads.GetOrCreateValue(transaction);
        bool retained = reads.Moves.TryGetValue(operationId, out var intent);
        intent ??= await ReadNamespaceMemberMoveRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (intent is null) return null;
        try
        {
            if (!retained)
            {
                await ValidateNamespaceMemberMoveReferencesAsync(intent, false, transaction, token).ConfigureAwait(false);
                reads.Moves.Add(operationId, intent);
            }
            if (validateRelocationChain && !reads.MoveChains.Contains(operationId))
            {
                _ = await ValidateNamespaceMemberMoveChainAsync(intent, transaction, token).ConfigureAwait(false);
                reads.MoveChains.Add(operationId);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The move intent lost its original member, protection or predecessor references.", error); }
        return intent;
    }

    private async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadNamespaceMemberMoveRowAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseNamespaceMemberMoveIntent intent;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id,member_id,root_id,source_parent_id,target_parent_id,target_name_key,payload,fingerprint,previous_move_id
                FROM namespace_member_moves WHERE operation_id=$id;
                """;
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            intent = ReadNamespaceMovePayload<MirrorPulseNamespaceMemberMoveIntent>((byte[])reader[6], (byte[])reader[7],
                NamespaceMemberMovePayloadLimit, ValidateNamespaceMemberMove);
            if (intent.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") ||
                reader.GetString(1) != intent.MemberId.ToString("D") || reader.GetString(2) != intent.RootId.ToString() ||
                reader.GetString(3) != intent.Source.ParentMemberId.ToString("D") || reader.GetString(4) != intent.Target.ParentMemberId.ToString("D") ||
                reader.GetString(5) != NamespaceMemberNameKey(intent.Target.RelativePath) ||
                (reader.IsDBNull(8) ? null : reader.GetString(8)) != intent.PreviousMoveOperationId?.ToString("D"))
                throw new InvalidDataException("The move indexes disagree with the retained intent.");
        }
        return intent;
    }

    private async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadPendingNamespaceMemberMoveCoreAsync(RootId rootId,
        SqliteTransaction transaction, CancellationToken token)
    {
        Guid operationId, memberId;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,member_id FROM namespace_member_move_pending WHERE root_id=$root;";
            query.Parameters.AddWithValue("$root", rootId.ToString());
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            operationId = ParseNamespaceMemberId(reader.GetString(0));
            memberId = ParseNamespaceMemberId(reader.GetString(1));
        }
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending move lost its original intent.");
        if (intent.RootId != rootId || intent.MemberId != memberId)
            throw new InvalidDataException("The pending move index refers to another member or root.");
        await RequireNamespaceMoveReservationAsync(intent, transaction, token).ConfigureAwait(false);
        return intent;
    }

    private async Task RequireNamespaceMoveReservationAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT parent_member_id,child_name_key FROM namespace_member_move_reservations WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", intent.OperationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != intent.Target.ParentMemberId.ToString("D") ||
            reader.GetString(1) != NamespaceMemberNameKey(intent.Target.RelativePath))
            throw new InvalidDataException("The unresolved move lost its reserved target name.");
    }

    private async Task<MirrorPulseNamespaceMemberMoveIntent> RequireNamespaceMemberMoveExecutionCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original move intent is missing.");
        var pending = await ReadPendingNamespaceMemberMoveCoreAsync(intent.RootId, transaction, token).ConfigureAwait(false);
        if (pending?.OperationId != operationId) throw new InvalidOperationException("Only the retained pending move can execute.");
        await ValidateNamespaceMemberMoveReferencesAsync(intent, true, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMoveTargetAvailableAsync(intent, transaction, token).ConfigureAwait(false);
        if (intent.DirectoryMoveEvidence is not null)
            await RequireCurrentNamespaceDirectoryMoveSnapshotAsync(intent, transaction, token).ConfigureAwait(false);
        return intent;
    }

    private async Task<MirrorPulseNamespaceMemberMoveStart?> ReadNamespaceMemberMoveStartCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token, bool validateRelocationChain = true)
    {
        MirrorPulseNamespaceMemberMoveStart start;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id,payload,fingerprint FROM namespace_member_move_starts WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            start = ReadNamespaceMovePayload<MirrorPulseNamespaceMemberMoveStart>((byte[])reader[1], (byte[])reader[2], 4096,
                value =>
                {
                    if (value.Version != 1 || value.OperationId != operationId || value.StartedAt == default)
                        throw new ArgumentException("The retained move start is invalid.");
                });
            if (reader.GetString(0) != operationId.ToString("D")) throw new InvalidDataException("The move start index is invalid.");
        }
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token, validateRelocationChain).ConfigureAwait(false)
            ?? throw new InvalidDataException("The start lost its original move intent.");
        if (start.StartedAt < intent.PreparedAt) throw new InvalidDataException("The start predates its original move admission.");
        return start;
    }

    private static T ReadNamespaceMovePayload<T>(byte[] payload, byte[] fingerprint, int limit, Action<T> validate) where T : class
    {
        if (payload.Length > limit || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The retained move payload fingerprint is invalid.");
        try
        {
            T value = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The move payload is empty.");
            validate(value);
            if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions)))
                throw new InvalidDataException("The retained move payload is not canonical.");
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        { throw new InvalidDataException("The retained move payload is invalid.", error); }
    }

    private static async Task EnsureNamespaceMemberMoveSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_member_moves (
                operation_id TEXT NOT NULL PRIMARY KEY,
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                root_id TEXT NOT NULL,
                source_parent_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                target_parent_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                target_name_key TEXT NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_move_pending (
                root_id TEXT NOT NULL PRIMARY KEY,
                member_id TEXT NOT NULL UNIQUE REFERENCES namespace_members(member_id),
                operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_moves(operation_id)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_move_reservations (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
                parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                child_name_key TEXT NOT NULL,
                UNIQUE(parent_member_id,child_name_key)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_move_starts (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_directory_move_snapshots (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_positions (
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                operation_id TEXT NOT NULL REFERENCES namespace_member_move_completions(operation_id),
                previous_operation_id TEXT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(member_id,operation_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_namespace_position_previous
                ON namespace_member_positions(member_id,previous_operation_id)
                WHERE previous_operation_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_namespace_position_operation
                ON namespace_member_positions(operation_id,member_id);
            CREATE TABLE IF NOT EXISTS namespace_directory_move_completions (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_completions(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
