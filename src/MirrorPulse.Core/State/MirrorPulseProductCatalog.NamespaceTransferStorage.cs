using System.Runtime.CompilerServices;
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
            copy = ReadNamespaceMovePayload<MirrorPulseNamespaceTransferCopy>((byte[])reader[5], (byte[])reader[6], 2_097_152, ValidateNamespaceTransferCopyProgress);
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
            IEnumerable<string> expectedRoots = copy.Acknowledgement?.AcknowledgedAt is null
                ? new[] { original.SourceRoute.RootId.ToString(), original.TargetRoute.RootId.ToString() }.Order(StringComparer.Ordinal) : [];
            if (!roots.SequenceEqual(expectedRoots))
                throw new InvalidDataException("Transfer fences disagree with the original pending or completed acknowledgement.");
            query.CommandText = "SELECT effect_id,kind FROM namespace_transfer_effects WHERE operation_id=$id ORDER BY kind;";
            var effects = new List<(string, int)>();
            await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) effects.Add((reader.GetString(0), reader.GetInt32(1)));
            if (!effects.SequenceEqual(new[] { (original.CopyOperationId.ToString("D"), 0), (original.DeleteOperationId.ToString("D"), 1) }))
                throw new InvalidDataException("The transfer lost its distinct original copy or source-delete effect.");
        }
        foreach (var snapshot in new[] { original.Source, original.SourceParent, original.TargetParent })
            await RequireRemoteUpdateHistoricalSnapshotAsync(snapshot, original.PreparedAt, transaction, token).ConfigureAwait(false);
        copy = copy with { TargetBirth = await ReadNamespaceTransferTargetBirthCoreAsync(copy, transaction, token).ConfigureAwait(false) };
        if (copy.TargetPublication is { } publication)
        {
            try { await ValidateNamespaceTransferPublicationReferencesAsync(copy, publication, transaction, token).ConfigureAwait(false); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FileNotFoundException)
            { throw new InvalidDataException("The transfer lost its original native target publication references.", error); }
        }
        await ValidateNamespaceTransferSourceDeletionReferencesAsync(copy, transaction, token).ConfigureAwait(false);
        await ValidateNamespaceTransferJournalIndexesAsync(copy, transaction, token).ConfigureAwait(false);
        await ValidateNamespaceTransferStateAsync(copy, transaction, token).ConfigureAwait(false);
        await ValidateNamespaceTransferAcknowledgementReferencesAsync(copy, transaction, token).ConfigureAwait(false);
        return copy;
    }

    internal const string NamespacePendingTransferPageSql = """
        SELECT operation_id FROM namespace_transfer_states
        WHERE completed=0 AND operation_id>$after ORDER BY operation_id LIMIT 16;
        """;

    private async IAsyncEnumerable<MirrorPulseNamespaceTransferCopy> ReadPendingNamespaceTransfersCoreAsync(
        SqliteTransaction transaction, [EnumeratorCancellation] CancellationToken token)
    {
        await RequireNamespaceTransferStateIntegrityAsync(transaction, token).ConfigureAwait(false);
        string after = string.Empty; int count = 0;
        while (true)
        {
            var ids = new List<Guid>(16);
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = NamespacePendingTransferPageSql;
                query.Parameters.AddWithValue("$after", after);
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            foreach (Guid id in ids)
            {
                if (++count > 4096) throw new InvalidDataException("The pending transfer inventory exceeds its recovery bound.");
                // Consumers keep the original catalog transaction. Only one complete graph and
                // one bounded ID page are retained; no partial recovery result is published.
                yield return await ReadNamespaceTransferCoreAsync(id, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("An original transfer fence or effect lost its admission.");
            }
            if (ids.Count < 16) yield break;
            after = ids[^1].ToString("D");
        }
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
        await using var state = _connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO namespace_transfer_states(operation_id,completed,payload_fingerprint) VALUES($id,$completed,$hash)
            ON CONFLICT(operation_id) DO UPDATE SET completed=$completed,payload_fingerprint=$hash;
            """;
        state.Parameters.AddWithValue("$id", copy.Plan.OperationId.ToString("D"));
        state.Parameters.AddWithValue("$completed", copy.Acknowledgement?.AcknowledgedAt is null ? 0 : 1);
        state.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        await state.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task RequireNotNamespaceTransferOwnerAsync(Guid id, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM namespace_transfers WHERE operation_id=$id UNION ALL SELECT 1 FROM namespace_transfer_effects WHERE effect_id=$id UNION ALL SELECT 1 FROM namespace_transfer_target_births WHERE birth_id=$id UNION ALL SELECT 1 FROM namespace_transfer_journal_members WHERE operation_id=$id UNION ALL SELECT 1 FROM retired_transfer_observations WHERE operation_id=$id LIMIT 1;";
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
