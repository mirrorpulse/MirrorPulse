using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task CreateRemoteTreeSettlementOwnerAsync(MirrorPulseNamespaceRetirementBatch batch,
        string startFingerprint, SqliteTransaction transaction, CancellationToken token)
    {
        var progress = new MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress(1, batch.OperationId,
            batch.RemoteEntry!.BatchFingerprint, batch.RemoteTreeFingerprint!, startFingerprint,
            batch.Entries.Count, batch.Entries.Count - 1, null, EmptyTreeSettlementDigest, null);
        byte[] payload = TreeSettlementPayload(progress);
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO namespace_remote_retirement_tree_settlements(operation_id,next_ordinal,payload,fingerprint) VALUES($id,$next,$payload,$hash);";
        insert.Parameters.AddWithValue("$id", batch.OperationId.ToString("D")); insert.Parameters.AddWithValue("$next", progress.NextOrdinal);
        insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress> ReadRemoteTreeSettlementProgressCoreAsync(
        MirrorPulseNamespaceRetirementBatch batch, MirrorPulseNamespaceRemoteRetirementTreeStart start,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT next_ordinal,payload,fingerprint FROM namespace_remote_retirement_tree_settlements WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", batch.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The original tree invocation lost its required settlement owner.");
        var progress = DecodeTreeSettlement<MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress>((byte[])reader[1], (byte[])reader[2]);
        if (progress.Version != 1 || progress.OperationId != batch.OperationId || progress.BatchFingerprint != batch.RemoteEntry!.BatchFingerprint ||
            progress.TreeFingerprint != batch.RemoteTreeFingerprint || progress.StartFingerprint != batch.RemoteTreeStartFingerprint ||
            progress.MemberCount != batch.Entries.Count || progress.NextOrdinal < -1 || progress.NextOrdinal >= progress.MemberCount ||
            reader.GetInt32(0) != progress.NextOrdinal || !IsTreeSettlementFingerprint(progress.SettledFingerprint) ||
            progress.OutcomeFingerprint is not null && !IsTreeSettlementFingerprint(progress.OutcomeFingerprint) ||
            progress.OutcomeFingerprint is null && progress.NextOrdinal != progress.MemberCount - 1 ||
            (progress.CompletedAt is not null) != (batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.Completed) ||
            progress.CompletedAt is not null && (progress.NextOrdinal != -1 || progress.OutcomeFingerprint is null))
            throw new InvalidDataException("The original tree settlement owner changed its immutable invocation or durable position.");
        await reader.DisposeAsync().ConfigureAwait(false);
        var outcome = await ReadRemoteTreeOutcomeCoreAsync(batch.OperationId, transaction, token).ConfigureAwait(false);
        if (outcome is null ? progress.OutcomeFingerprint is not null :
            progress.OutcomeFingerprint != Convert.ToHexString(SHA256.HashData(TreeSettlementPayload(outcome))))
            throw new InvalidDataException("The original tree result and its owning settlement reference disagree.");
        if (outcome is not null)
        {
            try { ValidateRemoteTreeOutcome(batch, start, outcome); }
            catch (InvalidOperationException error) { throw new InvalidDataException("The retained original SDK tree result is invalid.", error); }
            if (progress.CompletedAt < outcome.VerifiedAt) throw new InvalidDataException("Tree completion predates its SDK outcome.");
        }
        string digest = EmptyTreeSettlementDigest; int next = progress.MemberCount - 1;
        await using var rows = _connection.CreateCommand(); rows.Transaction = transaction;
        rows.CommandText = """
            SELECT m.ordinal,m.fingerprint,m.native_missing,n.member_id,m.payload,m.member_id
            FROM namespace_remote_retirement_tree_member_settlements m
            LEFT JOIN namespace_member_names n ON n.member_id=m.member_id
            WHERE m.operation_id=$id ORDER BY m.ordinal DESC;
            """;
        rows.Parameters.AddWithValue("$id", batch.OperationId.ToString("D"));
        await using var values = await rows.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await values.ReadAsync(token).ConfigureAwait(false))
        {
            byte[] hash = (byte[])values[1];
            if (values.GetInt32(0) != next || next <= progress.NextOrdinal || hash.Length != 32 ||
                values.GetInt32(2) == 1 && !values.IsDBNull(3))
                throw new InvalidDataException("The original tree member ledger lost its contiguous disposition or restored a removed name.");
            var member = DecodeTreeSettlement<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement>((byte[])values[4], hash);
            if (member.Version != 1 || member.OperationId != batch.OperationId || member.Ordinal != next ||
                member.MemberId != batch.Entries[next].MemberId || values.GetString(5) != member.MemberId.ToString("D") ||
                values.GetInt32(2) != (member.NativeMissing ? 1 : 0))
                throw new InvalidDataException("The original tree member ledger changed a committed disposition owner.");
            digest = ExtendTreeSettlementDigest(digest, next--, hash);
        }
        if (next != progress.NextOrdinal || digest != progress.SettledFingerprint)
            throw new InvalidDataException("The original tree member ledger lost committed history.");
        return progress;
    }

    private async Task WriteRemoteTreeSettlementProgressCoreAsync(MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress progress,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = TreeSettlementPayload(progress);
        await using var update = _connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE namespace_remote_retirement_tree_settlements SET next_ordinal=$next,payload=$payload,fingerprint=$hash WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$id", progress.OperationId.ToString("D")); update.Parameters.AddWithValue("$next", progress.NextOrdinal);
        update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The original tree settlement owner disappeared.");
    }

    private async Task InsertRemoteTreeOutcomeCoreAsync(Guid operation, byte[] payload,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO namespace_remote_retirement_tree_outcomes(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
        insert.Parameters.AddWithValue("$id", operation.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeOutcome?> ReadRemoteTreeOutcomeCoreAsync(Guid operation,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_remote_retirement_tree_outcomes WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operation.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = DecodeTreeSettlement<MirrorPulseNamespaceRemoteRetirementTreeOutcome>((byte[])reader[0], (byte[])reader[1]);
        if (result.OperationId != operation) throw new InvalidDataException("The original SDK tree result changed its operation.");
        return result;
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement?> ReadRemoteTreeMemberSettlementCoreAsync(
        MirrorPulseNamespaceRetirementBatch batch, int ordinal, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id,native_missing,payload,fingerprint FROM namespace_remote_retirement_tree_member_settlements WHERE operation_id=$id AND ordinal=$ordinal;";
        query.Parameters.AddWithValue("$id", batch.OperationId.ToString("D")); query.Parameters.AddWithValue("$ordinal", ordinal);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = DecodeTreeSettlement<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement>((byte[])reader[2], (byte[])reader[3]);
        if (ordinal < 0 || ordinal >= batch.Entries.Count || result.Ordinal != ordinal || result.OperationId != batch.OperationId ||
            result.MemberId != batch.Entries[ordinal].MemberId || reader.GetString(0) != result.MemberId.ToString("D") ||
            reader.GetInt32(1) != (result.NativeMissing ? 1 : 0))
            throw new InvalidDataException("The original member disposition changed its retained tree owner.");
        await reader.DisposeAsync().ConfigureAwait(false);
        var original = (await ReadRemoteRetirementTreePageCoreAsync(batch, ordinal - 1, 1, transaction, token).ConfigureAwait(false)).Entries[0];
        var outcome = await ReadRemoteTreeOutcomeCoreAsync(batch.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original member disposition lost its actual SDK root result.");
        try { ValidateRemoteTreeMemberSettlement(batch, outcome, original, result); }
        catch (InvalidOperationException error) { throw new InvalidDataException("The retained original member disposition is invalid.", error); }
        return result;
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeRetiredMember?> ReadRemovedRemoteTreeMemberCoreAsync(Guid memberId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,ordinal FROM namespace_remote_retirement_tree_member_settlements WHERE member_id=$member AND native_missing=1 LIMIT 2;";
        query.Parameters.AddWithValue("$member", memberId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        Guid operation = ParseNamespaceMemberId(reader.GetString(0)); int ordinal = reader.GetInt32(1);
        if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("A removed original member has competing tree owners.");
        await reader.DisposeAsync().ConfigureAwait(false);
        var batch = await RequireRemoteTreeSettlementBatchAsync(operation, transaction, token).ConfigureAwait(false);
        var start = (await ReadRemoteRetirementTreeStartCoreAsync(operation, batch, transaction, token).ConfigureAwait(false))!;
        var settlement = await ReadRemoteTreeMemberSettlementCoreAsync(batch, ordinal, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The tree-retired member lost its original disposition.");
        var original = (await ReadRemoteRetirementTreePageCoreAsync(batch, ordinal - 1, 1, transaction, token).ConfigureAwait(false)).Entries[0];
        if (!settlement.NativeMissing || original.Original.Context.Member.MemberId != memberId)
            throw new InvalidDataException("The tree-retired member does not match its original identity.");
        return new(operation, ordinal, original.Original, start.StartedAt, settlement.VerifiedAt);
    }

    private static T DecodeTreeSettlement<T>(byte[] payload, byte[] fingerprint)
    {
        if (payload.Length > 131_072 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The original tree settlement payload fingerprint is invalid.");
        T result;
        try { result = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The original tree settlement payload is empty."); }
        catch (JsonException error) { throw new InvalidDataException("The original tree settlement payload is invalid.", error); }
        if (!JsonSerializer.SerializeToUtf8Bytes(result, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The original tree settlement payload is not canonical.");
        return result;
    }

    private async Task RequireNoOrphanedRemoteTreeSettlementAsync(Guid operation, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM namespace_remote_retirement_tree_settlements WHERE operation_id=$id UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_outcomes WHERE operation_id=$id UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_member_settlements WHERE operation_id=$id LIMIT 1;";
        query.Parameters.AddWithValue("$id", operation.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) throw new InvalidDataException("Original tree settlement evidence lost its invocation start.");
    }

    private async Task RequireRemoteTreeSettlementOwnershipIntegrityAsync(SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT 1 FROM namespace_remote_retirement_tree_owners o
                LEFT JOIN namespace_member_retirement_batches b ON b.operation_id=o.operation_id WHERE b.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_starts s
                LEFT JOIN namespace_remote_retirement_tree_owners o ON o.operation_id=s.operation_id WHERE o.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_starts s
                LEFT JOIN namespace_remote_retirement_tree_settlements p ON p.operation_id=s.operation_id WHERE p.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_settlements p
                LEFT JOIN namespace_remote_retirement_tree_starts s ON s.operation_id=p.operation_id WHERE s.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_outcomes r
                LEFT JOIN namespace_remote_retirement_tree_settlements p ON p.operation_id=r.operation_id WHERE p.operation_id IS NULL
            UNION ALL SELECT 1 FROM namespace_remote_retirement_tree_member_settlements m
                LEFT JOIN namespace_remote_retirement_tree_settlements p ON p.operation_id=m.operation_id WHERE p.operation_id IS NULL
            LIMIT 1;
            """;
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Orphaned original SDK tree evidence cannot silently release a namespace dispatch fence.");
    }

    private async Task UpgradeRemoteTreeSettlementOwnersAsync(long previousVersion, CancellationToken token)
    {
        if (previousVersion >= 73) return;
        using var transaction = _connection.BeginTransaction();
        var operations = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT operation_id FROM namespace_remote_retirement_tree_starts ORDER BY operation_id LIMIT 1000002;";
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (operations.Count > 1_000_001) throw new InvalidDataException("The original tree invocation history exceeds its upgrade bound.");
        foreach (Guid operation in operations)
        {
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token, requireTreeSettlementOwner: false).ConfigureAwait(false)
                ?? throw new InvalidDataException("An existing original tree start lost its admission during upgrade.");
            if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared || batch.RemoteTreeStartFingerprint is null)
                throw new InvalidDataException("The older schema cannot contain an already settled original SDK tree.");
            await CreateRemoteTreeSettlementOwnerAsync(batch, batch.RemoteTreeStartFingerprint, transaction, token).ConfigureAwait(false);
            _ = await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token).ConfigureAwait(false);
        }
        await using var version = _connection.CreateCommand(); version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version=73;";
        await version.ExecuteNonQueryAsync(token).ConfigureAwait(false); transaction.Commit();
    }

    private static async Task EnsureRemoteTreeSettlementSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_settlements(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_starts(operation_id),
                next_ordinal INTEGER NOT NULL CHECK(next_ordinal>=-1),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_outcomes(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_settlements(operation_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_member_settlements(
                operation_id TEXT NOT NULL REFERENCES namespace_remote_retirement_tree_settlements(operation_id),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0),member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                native_missing INTEGER NOT NULL CHECK(native_missing IN (0,1)),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                PRIMARY KEY(operation_id,ordinal),UNIQUE(operation_id,member_id));
            CREATE UNIQUE INDEX IF NOT EXISTS namespace_remote_tree_removed_member ON namespace_remote_retirement_tree_member_settlements(member_id) WHERE native_missing=1;
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
