using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task ValidateNamespaceMovePredecessorClaimAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceRemoteMoveRejection? rejection, bool cancelled, SqliteTransaction transaction, CancellationToken token)
    {
        if (cancelled && rejection is not null) throw new InvalidDataException("One original move cannot be both remotely rejected and locally cancelled.");
        bool terminalWithoutMove = rejection is not null || cancelled;
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT previous_move_id FROM namespace_member_move_predecessor_claims WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        var claim = await query.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (claim != (!terminalWithoutMove ? intent.PreviousMoveOperationId?.ToString("D") : null))
            throw new InvalidDataException("The move lost its exclusive live predecessor claim.");
        if (!terminalWithoutMove)
        {
            query.CommandText = """
                SELECT EXISTS(SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$operation),
                       EXISTS(SELECT 1 FROM namespace_member_move_pending WHERE operation_id=$operation),
                       EXISTS(SELECT 1 FROM namespace_member_move_reservations WHERE operation_id=$operation);
                """;
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            _ = await reader.ReadAsync(token).ConfigureAwait(false);
            bool completed = reader.GetInt64(0) != 0, pending = reader.GetInt64(1) != 0, reserved = reader.GetInt64(2) != 0;
            if (completed ? pending || reserved : !pending || !reserved)
                throw new InvalidDataException("The move lost its pending ownership or verified terminal outcome.");
            return;
        }
        query.CommandText = """
            SELECT 1 FROM namespace_member_move_pending WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_reservations WHERE operation_id=$operation
            LIMIT 1;
            """;
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("A rejected or cancelled move still owns an active pending or name reservation.");
    }

    private static async Task EnsureNamespaceMoveRejectionSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_move_rejections (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_starts(operation_id),
                conflict_id TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_move_predecessor_claims (
                previous_move_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_completions(operation_id),
                operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_moves(operation_id)
            );
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (originalVersion < 61)
        {
            command.CommandText = """
                INSERT OR IGNORE INTO namespace_member_move_predecessor_claims(previous_move_id,operation_id)
                SELECT previous_move_id,operation_id FROM namespace_member_moves WHERE previous_move_id IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = """
                SELECT 1 FROM namespace_member_moves m
                LEFT JOIN namespace_member_move_predecessor_claims c ON c.operation_id=m.operation_id
                WHERE m.previous_move_id IS NOT NULL AND (c.previous_move_id IS NULL OR c.previous_move_id<>m.previous_move_id)
                LIMIT 1;
                """;
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("The legacy move history cannot establish exclusive predecessor claims.");
        }
        command.CommandText = """
            DROP INDEX IF EXISTS ux_namespace_move_previous;
            CREATE INDEX IF NOT EXISTS ix_namespace_move_previous ON namespace_member_moves(previous_move_id);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
