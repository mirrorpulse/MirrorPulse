using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<MirrorPulseNamespaceRemoteUpdate?> ReadRemoteUpdateCoreAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        if (id == Guid.Empty) throw new ArgumentException("An original remote update ID is required.", nameof(id));
        MirrorPulseNamespaceRemoteUpdate retained; byte[] retainedFingerprint;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,root_id,member_id,finished,payload,fingerprint FROM namespace_remote_updates WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            byte[] payload = (byte[])reader[9], fingerprint = (byte[])reader[10];
            retainedFingerprint = fingerprint;
            if (payload.Length > 1_048_576 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
                throw new InvalidDataException("The original remote update fingerprint is invalid.");
            try
            {
                retained = JsonSerializer.Deserialize<MirrorPulseNamespaceRemoteUpdate>(payload, TopologyJsonOptions)
                    ?? throw new InvalidDataException("The original remote update is empty.");
                ValidateRemoteUpdatePlan(retained.Plan);
                if (retained.StartedAt is { } start && start < retained.Plan.PreparedAt)
                    throw new ArgumentException("The original update start predates its admission.");
                if (retained.Outcome is { } outcome) ValidateRemoteUpdateOutcome(retained, outcome);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("The retained remote update state is inconsistent.", error); }
            var plan = retained.Plan;
            if (plan.OperationId != id || reader.GetString(0) != plan.InstanceId.ToString() || reader.GetString(1) != plan.BatchId ||
                reader.GetString(2) != plan.BatchFingerprint || reader.GetInt32(3) != plan.BatchEntryCount || reader.GetInt32(4) != plan.Ordinal ||
                reader.GetString(5) != plan.ChangeId || reader.GetString(6) != plan.Child.Context.Member.RootId!.Value.ToString() ||
                reader.GetString(7) != plan.Child.Context.Member.MemberId.ToString("D") || reader.GetInt32(8) != (retained.Outcome is null ? 0 : 1) ||
                !RemoteUpdatePayload(retained).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The remote update index or canonical payload changed its original owner.");
        }
        await using (var completion = _connection.CreateCommand())
        {
            completion.Transaction = transaction;
            completion.CommandText = "SELECT owner_fingerprint FROM namespace_remote_update_completions WHERE operation_id=$id;";
            completion.Parameters.AddWithValue("$id", id.ToString("D"));
            var marker = await completion.ExecuteScalarAsync(token).ConfigureAwait(false) as byte[];
            if (retained.Outcome is null ? marker is not null : marker is null || !marker.AsSpan().SequenceEqual(retainedFingerprint))
                throw new InvalidDataException("The terminal remote update lost its exact durable completion or an unfinished update acquired one.");
        }
        await using (var pending = _connection.CreateCommand())
        {
            pending.Transaction = transaction; pending.CommandText = "SELECT root_id FROM namespace_remote_update_pending WHERE operation_id=$id;";
            pending.Parameters.AddWithValue("$id", id.ToString("D"));
            object? root = await pending.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (retained.Outcome is null ? root as string != retained.Plan.Child.Context.Member.RootId!.Value.ToString() : root is not null)
                throw new InvalidDataException("The remote update lost its pending root ownership or retained a terminal fence.");
        }
        var original = retained.Plan;
        await ValidateNamespaceRemoteEntryCrossKindAsync(id, original.InstanceId, original.BatchId, original.BatchFingerprint,
            original.BatchEntryCount, original.Ordinal, original.ChangeId, 4, transaction, token).ConfigureAwait(false);
        await RequireRemoteUpdateHistoricalSnapshotAsync(original.Child, original.PreparedAt, transaction, token).ConfigureAwait(false);
        await RequireRemoteUpdateHistoricalSnapshotAsync(original.Parent, original.PreparedAt, transaction, token).ConfigureAwait(false);
        return retained;
    }

    private async Task WriteRemoteUpdateCoreAsync(MirrorPulseNamespaceRemoteUpdate retained, SqliteTransaction transaction, CancellationToken token, bool insert = false)
    {
        var plan = retained.Plan; byte[] payload = RemoteUpdatePayload(retained);
        await using var write = _connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = insert ? """
            INSERT INTO namespace_remote_updates(operation_id,instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,root_id,member_id,finished,payload,fingerprint)
            VALUES($id,$instance,$batch,$batchHash,$count,$ordinal,$change,$root,$member,$finished,$payload,$hash);
            """ : "UPDATE namespace_remote_updates SET finished=$finished,payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        write.Parameters.AddWithValue("$id", plan.OperationId.ToString("D"));
        write.Parameters.AddWithValue("$finished", retained.Outcome is null ? 0 : 1); write.Parameters.AddWithValue("$payload", payload);
        write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (insert)
        {
            write.Parameters.AddWithValue("$instance", plan.InstanceId.ToString()); write.Parameters.AddWithValue("$batch", plan.BatchId);
            write.Parameters.AddWithValue("$batchHash", plan.BatchFingerprint); write.Parameters.AddWithValue("$count", plan.BatchEntryCount);
            write.Parameters.AddWithValue("$ordinal", plan.Ordinal); write.Parameters.AddWithValue("$change", plan.ChangeId);
            write.Parameters.AddWithValue("$root", plan.Child.Context.Member.RootId!.Value.ToString());
            write.Parameters.AddWithValue("$member", plan.Child.Context.Member.MemberId.ToString("D"));
        }
        if (await write.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The original remote update could not be retained.");
    }

    private static byte[] RemoteUpdatePayload(MirrorPulseNamespaceRemoteUpdate retained)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retained, TopologyJsonOptions);
        if (payload.Length > 1_048_576) throw new InvalidDataException("The original remote update exceeds its catalog bound.");
        return payload;
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceRemoteUpdate>> ReadPendingRemoteUpdatesCoreAsync(SqliteTransaction transaction, CancellationToken token)
    {
        var ids = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT u.operation_id FROM namespace_remote_updates u LEFT JOIN namespace_remote_update_completions c ON c.operation_id=u.operation_id
                    WHERE u.finished<>1 OR c.operation_id IS NULL OR c.owner_fingerprint<>u.fingerprint
                UNION SELECT operation_id FROM namespace_remote_update_pending
                UNION SELECT c.operation_id FROM namespace_remote_update_completions c LEFT JOIN namespace_remote_updates u ON u.operation_id=c.operation_id
                    WHERE u.operation_id IS NULL OR u.finished<>1 OR c.owner_fingerprint<>u.fingerprint LIMIT 4097;
                """;
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (ids.Count > 4096) throw new InvalidDataException("Pending remote updates exceed the bounded root inventory.");
        var result = new List<MirrorPulseNamespaceRemoteUpdate>(ids.Count);
        foreach (Guid id in ids)
            result.Add(await ReadRemoteUpdateCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("A pending remote update lost its original admission."));
        return result.AsReadOnly();
    }

    private static async Task EnsureNamespaceRemoteUpdateSchemaAsync(SqliteConnection connection, CancellationToken token, bool stampVersion = false)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_updates(
                operation_id TEXT PRIMARY KEY,instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
                entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 1 AND 65536),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),
                change_id TEXT NOT NULL,root_id TEXT NOT NULL,member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                finished INTEGER NOT NULL CHECK(finished IN (0,1)),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
            CREATE INDEX IF NOT EXISTS namespace_remote_updates_pending ON namespace_remote_updates(finished,root_id,operation_id);
            CREATE TABLE IF NOT EXISTS namespace_remote_update_pending(root_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_remote_updates(operation_id));
            CREATE TABLE IF NOT EXISTS namespace_remote_update_completions(operation_id TEXT PRIMARY KEY REFERENCES namespace_remote_updates(operation_id),
                owner_fingerprint BLOB NOT NULL CHECK(length(owner_fingerprint)=32));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (stampVersion)
        {
            schema.CommandText = "PRAGMA user_version=79;"; await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
