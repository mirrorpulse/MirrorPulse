using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseNamespaceTransferCopy?> ReadNamespaceTransferCoreAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseNamespaceTransferCopy copy;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT source_root_id,target_root_id,copy_id,delete_id,content_id,payload,fingerprint FROM namespace_transfers WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            copy = ReadNamespaceMovePayload<MirrorPulseNamespaceTransferCopy>((byte[])reader[5], (byte[])reader[6], 2_097_152, value =>
            {
                ValidateNamespaceTransferPlan(value.Plan);
                if (value.CopyStartedAt is { } start && start < value.Plan.PreparedAt ||
                    (value.AcceptedRevision is null) != (value.CopyAcceptedAt is null) ||
                    value.CopyAcceptedAt is { } accepted && (value.CopyStartedAt is null || accepted < value.CopyStartedAt ||
                        InvalidRemoteUpdateText(value.AcceptedRevision, 4096)))
                    throw new ArgumentException("Transfer copy progress lost its original start or receipt chronology.");
            });
            var plan = copy.Plan;
            if (plan.OperationId != id || reader.GetString(0) != plan.SourceRoute.RootId.ToString() ||
                reader.GetString(1) != plan.TargetRoute.RootId.ToString() || reader.GetString(2) != plan.CopyOperationId.ToString("D") ||
                reader.GetString(3) != plan.DeleteOperationId.ToString("D") || reader.GetString(4) != plan.PrivateContentId.ToString("D"))
                throw new InvalidDataException("Transfer indexes disagree with their immutable source, target, effects or content.");
        }
        var original = copy.Plan;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$id", id.ToString("D"));
            query.CommandText = "SELECT root_id FROM namespace_transfer_roots WHERE operation_id=$id ORDER BY root_id;";
            var roots = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) roots.Add(reader.GetString(0));
            if (!roots.SequenceEqual(new[] { original.SourceRoute.RootId.ToString(), original.TargetRoute.RootId.ToString() }.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("A pending transfer must retain both exact root fences.");
            query.CommandText = "SELECT effect_id,kind FROM namespace_transfer_effects WHERE operation_id=$id ORDER BY kind;";
            var effects = new List<(string, int)>();
            await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) effects.Add((reader.GetString(0), reader.GetInt32(1)));
            if (!effects.SequenceEqual(new[] { (original.CopyOperationId.ToString("D"), 0), (original.DeleteOperationId.ToString("D"), 1) }))
                throw new InvalidDataException("The transfer lost its distinct original copy or source-delete effect.");
        }
        foreach (var snapshot in new[] { original.Source, original.SourceParent, original.TargetParent })
            await RequireRemoteUpdateHistoricalSnapshotAsync(snapshot, original.PreparedAt, transaction, token).ConfigureAwait(false);
        return copy;
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceTransferCopy>> ReadPendingNamespaceTransfersCoreAsync(
        SqliteTransaction transaction, CancellationToken token)
    {
        var ids = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id FROM namespace_transfers
                UNION SELECT operation_id FROM namespace_transfer_roots
                UNION SELECT operation_id FROM namespace_transfer_effects ORDER BY operation_id LIMIT 4097;
                """;
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (ids.Count > 4096) throw new InvalidDataException("The pending transfer inventory exceeds its recovery bound.");
        var result = new List<MirrorPulseNamespaceTransferCopy>(ids.Count);
        foreach (Guid id in ids)
            result.Add(await ReadNamespaceTransferCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("An original transfer fence or effect lost its admission."));
        return result.AsReadOnly();
    }

    private async Task WriteNamespaceTransferCoreAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token, bool insert = false)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(copy, TopologyJsonOptions);
        if (payload.Length > 2_097_152) throw new ArgumentException("Transfer admission and progress must be bounded.", nameof(copy));
        await using var write = _connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = insert ? """
            INSERT INTO namespace_transfers(operation_id,source_root_id,target_root_id,copy_id,delete_id,content_id,payload,fingerprint)
            VALUES($id,$source,$target,$copy,$delete,$content,$payload,$hash);
            """ : "UPDATE namespace_transfers SET payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        write.Parameters.AddWithValue("$id", copy.Plan.OperationId.ToString("D"));
        write.Parameters.AddWithValue("$payload", payload); write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (insert)
        {
            write.Parameters.AddWithValue("$source", copy.Plan.SourceRoute.RootId.ToString());
            write.Parameters.AddWithValue("$target", copy.Plan.TargetRoute.RootId.ToString());
            write.Parameters.AddWithValue("$copy", copy.Plan.CopyOperationId.ToString("D"));
            write.Parameters.AddWithValue("$delete", copy.Plan.DeleteOperationId.ToString("D"));
            write.Parameters.AddWithValue("$content", copy.Plan.PrivateContentId.ToString("D"));
        }
        if (await write.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The original transfer could not be retained.");
    }

    private async Task RequireNotNamespaceTransferOwnerAsync(Guid id, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM namespace_transfers WHERE operation_id=$id UNION ALL SELECT 1 FROM namespace_transfer_effects WHERE effect_id=$id LIMIT 1;";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An original transfer owns this operation or effect ID.");
    }

    private static async Task EnsureNamespaceTransferSchemaAsync(SqliteConnection connection, bool stampVersion, bool requireExisting, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        if (requireExisting)
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('namespace_transfers','namespace_transfer_roots','namespace_transfer_effects');";
            if (Convert.ToInt64(await schema.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 3)
                throw new InvalidDataException("Schema 80 lost its original transfer ownership tables; they cannot be replaced with empty history.");
        }
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_transfers(
                operation_id TEXT PRIMARY KEY,source_root_id TEXT NOT NULL,target_root_id TEXT NOT NULL CHECK(target_root_id<>source_root_id),
                copy_id TEXT NOT NULL UNIQUE,delete_id TEXT NOT NULL UNIQUE,content_id TEXT NOT NULL UNIQUE,
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE TABLE IF NOT EXISTS namespace_transfer_roots(root_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id));
            CREATE INDEX IF NOT EXISTS namespace_transfer_root_owners ON namespace_transfer_roots(operation_id);
            CREATE TABLE IF NOT EXISTS namespace_transfer_effects(effect_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),kind INTEGER NOT NULL CHECK(kind IN (0,1)),UNIQUE(operation_id,kind));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (stampVersion)
        {
            schema.CommandText = "PRAGMA user_version=80;"; await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
