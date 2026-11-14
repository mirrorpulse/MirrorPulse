using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string NamespaceIdentityMoveColumn = "previous_move_id TEXT NULL REFERENCES journal_directory_moves(operation_id)";

    private static string DirectoryAcceptanceTableWithMoveLink(string original) =>
        original.Replace("CHECK((receipt IS NULL)", NamespaceIdentityMoveColumn + ",\nCHECK((receipt IS NULL)", StringComparison.Ordinal);

    private static readonly (string Table, string First, string Move, string Filter)[] NamespaceIdentityMoveIndexes =
    [
        ("namespace_member_identity_acceptances", "ux_namespace_identity_first", "ux_namespace_identity_move", ""),
        ("directory_acceptances", "ux_directory_identity_first", "ux_directory_identity_move", " AND receipt IS NOT NULL"),
    ];

    private static async Task EnsureNamespaceIdentityMoveLinksSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var (table, first, move, filter) in NamespaceIdentityMoveIndexes)
        {
            string firstSql = $"CREATE UNIQUE INDEX {first} ON {table}(member_id) WHERE previous_operation_id IS NULL{filter} AND previous_move_id IS NULL";
            string moveSql = $"CREATE UNIQUE INDEX {move} ON {table}(member_id,previous_move_id) WHERE previous_move_id IS NOT NULL{filter}";
            command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name='previous_move_id';";
            bool hasMoveLink = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
            if (!hasMoveLink && originalVersion < 110)
            {
                command.CommandText = $"""
                    ALTER TABLE {table} ADD COLUMN {NamespaceIdentityMoveColumn};
                    DROP INDEX {first};
                    {firstSql};
                    {moveSql};
                    """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (hasMoveLink && originalVersion < 110)
            {
                command.CommandText = $"SELECT 1 FROM {table} WHERE previous_move_id IS NOT NULL LIMIT 1;";
                if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("An older catalog cannot authorize a retained directory Move predecessor.");
            }
            foreach (var (name, expected) in new[] { (first, firstSql), (move, moveSql) })
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";
                command.Parameters.AddWithValue("$name", name);
                if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string existing ||
                    NormalizeRetiredTransferIndexSql(existing) != NormalizeRetiredTransferIndexSql(expected))
                    throw new InvalidDataException("The member identity predecessor index changed.");
            }
            command.CommandText = $"SELECT 1 FROM pragma_foreign_key_list('{table}') WHERE [from]='previous_move_id' AND [table]='journal_directory_moves' AND [to]='operation_id';";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
                throw new InvalidDataException("The member identity predecessor lost its original Move reference.");
            command.Parameters.Clear();
        }
        if (originalVersion < 110)
        {
            command.CommandText = "PRAGMA user_version=110;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task BindNamespaceIdentityPredecessorAsync(SqliteCommand command, Guid memberId, Guid? predecessor,
        SqliteTransaction transaction, CancellationToken token)
    {
        bool isMove = predecessor is { } id &&
            await ReadJournalDirectoryMoveProjectionIndexAsync(memberId, id, transaction, token).ConfigureAwait(false) is not null;
        command.Parameters.AddWithValue("$previous", !isMove && predecessor is { } ordinary ? ordinary.ToString("D") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$previousMove", isMove && predecessor is { } move ? move.ToString("D") : (object)DBNull.Value);
    }

    private static string? ReadNamespaceIdentityPredecessor(SqliteDataReader reader, int ordinary, int move)
    {
        if (reader.IsDBNull(move)) return reader.IsDBNull(ordinary) ? null : reader.GetString(ordinary);
        if (!reader.IsDBNull(ordinary)) throw new InvalidDataException("A member identity has two retained predecessors.");
        return reader.GetString(move);
    }
}
