using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string KeepRemoteAcknowledgementSchema = """
        CREATE TABLE conflict_keep_remote_acknowledgements(
            command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_file_starts(command_id),
            invocation_id TEXT NOT NULL UNIQUE,
            start_payload BLOB NOT NULL CHECK(length(start_payload)<=4096),
            start_fingerprint BLOB NOT NULL CHECK(length(start_fingerprint)=32),
            result_payload BLOB CHECK(length(result_payload)<=3145728),
            result_fingerprint BLOB CHECK(length(result_fingerprint)=32),
            CHECK((result_payload IS NULL)=(result_fingerprint IS NULL)))
        """;

    private static async Task EnsureKeepRemoteAcknowledgementSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='conflict_keep_remote_acknowledgements';";
        object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (originalVersion >= 102) throw new InvalidDataException("The catalog lost its KeepRemote acknowledgement table.");
            query.CommandText = KeepRemoteAcknowledgementSchema;
            await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(KeepRemoteAcknowledgementSchema))
            throw new InvalidDataException("The KeepRemote acknowledgement schema changed.");

        if (originalVersion < 102)
        {
            query.CommandText = "SELECT 1 FROM conflict_keep_remote_acknowledgements LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot adopt KeepRemote acknowledgement history.");
            query.CommandText = "PRAGMA user_version=102;";
            await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
