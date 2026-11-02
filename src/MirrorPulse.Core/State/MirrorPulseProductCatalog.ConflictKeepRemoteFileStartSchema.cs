using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static readonly (string Type, string Name, string Sql)[] KeepRemoteFileStartSchema =
    [
        ("table", "conflict_keep_remote_file_starts", """
            CREATE TABLE conflict_keep_remote_file_starts(
                command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_preparations(command_id),
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                root_id TEXT NOT NULL,
                payload BLOB NOT NULL CHECK(length(payload)<=1048576),
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("table", "conflict_keep_remote_file_pending", """
            CREATE TABLE conflict_keep_remote_file_pending(
                root_id TEXT NOT NULL PRIMARY KEY,
                command_id TEXT NOT NULL UNIQUE REFERENCES conflict_keep_remote_file_starts(command_id))
            """),
        ("index", "ix_keep_remote_file_start_root", "CREATE INDEX ix_keep_remote_file_start_root ON conflict_keep_remote_file_starts(root_id)")
    ];

    private static async Task EnsureKeepRemoteFileStartSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        foreach (var definition in KeepRemoteFileStartSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                if (originalVersion >= 96) throw new InvalidDataException("Schema96 lost its original KeepRemote file-start ownership.");
                query.Parameters.Clear(); query.CommandText = definition.Sql; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("KeepRemote cannot adopt another file-start ownership schema definition.");
        }
        if (originalVersion < 96)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM conflict_keep_remote_file_starts UNION ALL SELECT 1 FROM conflict_keep_remote_file_pending LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent KeepRemote file-start history.");
            query.CommandText = "PRAGMA user_version=96;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task<MirrorPulseConflictKeepRemoteFileStart?> ReadPendingKeepRemoteFileStartForRootCoreAsync(RootId root,
        SqliteTransaction transaction, CancellationToken token)
    {
        Guid? commandId = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT s.command_id,p.command_id,s.root_id,p.root_id FROM conflict_keep_remote_file_starts s
                LEFT JOIN conflict_keep_remote_file_pending p ON p.command_id=s.command_id WHERE s.root_id=$root
                UNION ALL
                SELECT s.command_id,p.command_id,s.root_id,p.root_id FROM conflict_keep_remote_file_pending p
                LEFT JOIN conflict_keep_remote_file_starts s ON s.command_id=p.command_id WHERE p.root_id=$root AND s.command_id IS NULL;
                """;
            query.Parameters.AddWithValue("$root", root.ToString());
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (commandId is not null || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) ||
                    reader.GetString(0) != reader.GetString(1) || reader.GetString(2) != root.ToString() || reader.GetString(3) != root.ToString() ||
                    !Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty)
                    throw new InvalidDataException("KeepRemote lost its complete original file-start/root-fence ownership.");
                commandId = id;
            }
        }
        return commandId is { } value ? await ReadKeepRemoteFileStartCoreAsync(value, transaction, token).ConfigureAwait(false) : null;
    }

    private async Task AddKeepRemoteFileStartRootFencesAsync(HashSet<RootId> roots, SqliteTransaction transaction, CancellationToken token)
    {
        var pendingRoots = new List<RootId>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT root_id FROM conflict_keep_remote_file_starts UNION SELECT root_id FROM conflict_keep_remote_file_pending;";
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string value = reader.GetString(0);
                if (!RootId.TryParse(value, out var root) || root.ToString() != value) throw new InvalidDataException("The KeepRemote file-start root is invalid.");
                pendingRoots.Add(root);
            }
        }
        foreach (var root in pendingRoots)
        {
            _ = await ReadPendingKeepRemoteFileStartForRootCoreAsync(root, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("An unfinished KeepRemote file start lost its original root fence.");
            roots.Add(root);
        }
    }
}
