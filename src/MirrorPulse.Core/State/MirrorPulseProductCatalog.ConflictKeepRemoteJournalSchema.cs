using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string KeepRemoteJournalBindingSchema = """
        CREATE TABLE conflict_keep_remote_journal_bindings(
            command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_preparations(command_id),
            payload BLOB NOT NULL CHECK(length(payload)<=16777216),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;

    private static async Task EnsureKeepRemoteJournalBindingSchemaAsync(SqliteConnection connection, long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='conflict_keep_remote_journal_bindings';";
        object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (originalVersion >= 95) throw new InvalidDataException("Schema95 lost original KeepRemote journal correlations.");
            query.CommandText = KeepRemoteJournalBindingSchema; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(KeepRemoteJournalBindingSchema))
            throw new InvalidDataException("KeepRemote cannot adopt another journal correlation schema definition.");
        if (originalVersion < 95)
        {
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_journal_bindings LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent retained KeepRemote journal correlations.");
            query.CommandText = "PRAGMA user_version=95;"; await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
