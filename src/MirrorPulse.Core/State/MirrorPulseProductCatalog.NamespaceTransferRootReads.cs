using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    internal const string NamespaceTransferRootCandidatesSql = """
        SELECT operation_id FROM (
            SELECT operation_id FROM namespace_transfers INDEXED BY namespace_transfer_pending_source_root
            WHERE source_root_id=$root AND json_extract(CAST(payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL LIMIT 2)
        UNION ALL SELECT operation_id FROM (
            SELECT operation_id FROM namespace_transfers INDEXED BY namespace_transfer_pending_target_root
            WHERE target_root_id=$root AND json_extract(CAST(payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL LIMIT 2)
        UNION ALL SELECT operation_id FROM namespace_transfer_roots WHERE root_id=$root;
        """;

    /// <summary>Reads only this root's original pending owner, without treating its reservation as authority.</summary>
    /// <remarks>The payload sides detect lost state/fence rows; the reservation side detects foreign or terminal owners.
    /// This is a read-only status lookup. Full recovery integrity and live dispatch admission remain separate.</remarks>
    private async Task<MirrorPulseNamespaceTransferCopy?> ReadPendingNamespaceTransferForRootCoreAsync(
        RootId rootId, SqliteTransaction transaction, CancellationToken token)
    {
        // A state with no original admission has no trustworthy root. Preserve this
        // global fail-closed check; the root indexes cannot safely classify it away.
        await using (var orphan = _connection.CreateCommand())
        {
            orphan.Transaction = transaction;
            orphan.CommandText = """
                SELECT 1 FROM namespace_transfer_states s LEFT JOIN namespace_transfers t USING(operation_id)
                WHERE t.operation_id IS NULL LIMIT 1;
                """;
            if (await orphan.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An orphaned transfer state cannot be assigned to a root status lookup.");
        }
        var candidates = new HashSet<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = NamespaceTransferRootCandidatesSql;
            query.Parameters.AddWithValue("$root", rootId.ToString());
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                candidates.Add(ParseNamespaceMemberId(reader.GetString(0)));
                if (candidates.Count > 1) throw new InvalidDataException("A root cannot acquire multiple original pending transfer owners.");
            }
        }
        if (candidates.Count == 0) return null;
        var copy = await ReadNamespaceTransferCoreAsync(candidates.Single(), transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("This root's transfer reservation lost its original admission.");
        if (copy.Acknowledgement?.AcknowledgedAt is not null ||
            copy.Plan.SourceRoute.RootId != rootId && copy.Plan.TargetRoute.RootId != rootId)
            throw new InvalidDataException("The pending root lookup disagrees with its original transfer routes or completion.");
        return copy;
    }

    private static async Task EnsureNamespaceTransferRootReadIndexesAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        foreach (var side in new[] { "source", "target" })
        {
            string name = "namespace_transfer_pending_" + side + "_root";
            string sql = "CREATE INDEX " + name + " ON namespace_transfers(" + side + "_root_id,operation_id) " +
                "WHERE json_extract(CAST(payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL";
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";
            schema.Parameters.Clear(); schema.Parameters.AddWithValue("$name", name);
            object? existing = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (existing is null)
            {
                // These indexes derive only from existing originals. Rebuilding them changes no
                // payload, receipt, pending reservation, historical completion or schema version.
                schema.Parameters.Clear(); schema.CommandText = sql;
                await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else if (existing is not string definition || NormalizeRetiredTransferIndexSql(definition) != NormalizeRetiredTransferIndexSql(sql))
                throw new InvalidDataException("The transfer root lookup lost its bounded pending-route index.");
        }
        transaction.Commit();
    }
}
