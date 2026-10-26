using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string RootLabelJournalUnfinishedPayload = "(json_type(CAST(payload AS TEXT),'$.AcknowledgedAt') IS NULL OR json_type(CAST(payload AS TEXT),'$.AcknowledgedAt')='null')";
    private static readonly (string Type, string Name, string Sql)[] RootLabelJournalSchema =
    [
        ("table", "root_label_journal_acknowledgements", """
            CREATE TABLE root_label_journal_acknowledgements(
                operation_id TEXT PRIMARY KEY,root_id TEXT NOT NULL,
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                label_operation_id TEXT NOT NULL REFERENCES managed_root_rename_history(operation_id),
                started_utc TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN (0,1)),
                payload BLOB NOT NULL CHECK(length(payload)<=262144),fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
            """),
        ("index", "root_label_journal_pending_status", "CREATE INDEX root_label_journal_pending_status ON root_label_journal_acknowledgements(operation_id,root_id) WHERE status=0"),
        ("index", "root_label_journal_pending_payload", "CREATE INDEX root_label_journal_pending_payload ON root_label_journal_acknowledgements(operation_id,root_id) WHERE " + RootLabelJournalUnfinishedPayload),
        ("index", "root_label_journal_pending_root", "CREATE UNIQUE INDEX root_label_journal_pending_root ON root_label_journal_acknowledgements(root_id) WHERE status=0")
    ];

    private static async Task EnsureRootLabelJournalSchemaAsync(SqliteConnection connection, long originalVersion,
        bool stampVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name='root_label_journal_acknowledgements') OR (type='index' AND name IN ('root_label_journal_pending_status','root_label_journal_pending_payload','root_label_journal_pending_root'));";
        if (originalVersion >= 90 && (long)(await query.ExecuteScalarAsync(token).ConfigureAwait(false))! != 4)
            throw new InvalidDataException("Schema90 lost original root journal ownership or a required bounded recovery index.");
        foreach (var definition in RootLabelJournalSchema)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            query.Parameters.AddWithValue("$type", definition.Type); query.Parameters.AddWithValue("$name", definition.Name);
            object? existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                query.Parameters.Clear(); query.CommandText = definition.Sql;
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(definition.Sql))
                throw new InvalidDataException("The original root journal ownership schema cannot be replaced by another definition.");
        }
        if (originalVersion < 90)
        {
            query.Parameters.Clear(); query.CommandText = "SELECT 1 FROM root_label_journal_acknowledgements LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An older catalog cannot invent later root journal ACK ownership.");
            if (stampVersion)
            {
                query.CommandText = "PRAGMA user_version=90;";
                await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        transaction.Commit();
    }

    private async Task<IReadOnlyList<Guid>> ReadRootLabelJournalPendingIdsCoreAsync(RootId? root, Guid? after,
        int limit, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT operation_id FROM (
                SELECT operation_id FROM root_label_journal_acknowledgements INDEXED BY root_label_journal_pending_status
                WHERE status=0 AND ($root IS NULL OR root_id=$root) AND ($after IS NULL OR operation_id>$after)
                ORDER BY operation_id LIMIT $limit)
            UNION SELECT operation_id FROM (
                SELECT operation_id FROM root_label_journal_acknowledgements INDEXED BY root_label_journal_pending_payload
                WHERE (json_type(CAST(payload AS TEXT),'$.AcknowledgedAt') IS NULL OR json_type(CAST(payload AS TEXT),'$.AcknowledgedAt')='null')
                    AND ($root IS NULL OR root_id=$root) AND ($after IS NULL OR operation_id>$after)
                ORDER BY operation_id LIMIT $limit)
            ORDER BY operation_id LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$root", (object?)root?.ToString() ?? DBNull.Value);
        query.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
        query.Parameters.AddWithValue("$limit", limit);
        var result = new List<Guid>();
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(ParseNamespaceMemberId(reader.GetString(0)));
        return result.AsReadOnly();
    }

    private async Task<MirrorPulseRootLabelJournalAcknowledgement?> ReadPendingRootLabelJournalForRootCoreAsync(
        RootId root, SqliteTransaction transaction, CancellationToken token)
    {
        var ids = await ReadRootLabelJournalPendingIdsCoreAsync(root, null, 2, transaction, token).ConfigureAwait(false);
        if (ids.Count > 1) throw new InvalidDataException("One root acquired multiple unfinished original ACK owners.");
        return ids.Count == 0 ? null : await ReadRootLabelJournalCoreAsync(ids[0], transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The root journal fence lost its original ACK owner.");
    }

    private async Task AddRootLabelJournalFencesAsync(HashSet<RootId> roots, SqliteTransaction transaction, CancellationToken token)
    {
        var ids = await ReadRootLabelJournalPendingIdsCoreAsync(null, null, 4097, transaction, token).ConfigureAwait(false);
        if (ids.Count > 4096) throw new InvalidDataException("Pending root ACK discovery exceeds the Host's root inventory bound.");
        foreach (Guid id in ids)
        {
            var retained = await ReadRootLabelJournalCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A root journal fence lost its immutable first start.");
            roots.Add(retained.Witness.RootId);
        }
    }
}
