using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Records that one original SDK subtree invocation may have begun, not that it succeeded.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeStart(int Version, Guid OperationId,
    string BatchFingerprint, string TreeFingerprint, DateTimeOffset StartedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Commits the original start before the Host may invoke the SDK's unchanged directory delete.</summary>
    /// <remarks>The Host must inspect the actual native objects and public SDK projections before calling this method.
    /// This method validates retained catalog facts; it performs no native operation and proves no SDK outcome.</remarks>
    public async Task<MirrorPulseNamespaceRemoteRetirementTreeStart> RecordNamespaceRemoteRetirementTreeStartAsync(
        Guid operationId, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || startedAt == default) throw new ArgumentException("An original SDK tree start requires its operation and timestamp.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original SDK tree admission is missing.");
            if (batch.Version != 2 || batch.RemoteEntry is not { } remote || startedAt < batch.PreparedAt)
                throw new ArgumentException("The start requires its original SDK tree and a valid chronology.");
            if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared)
                throw new InvalidOperationException("The original SDK tree cannot begin from a terminal or recovery-required admission.");
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteRetirementTreeId: operationId).ConfigureAwait(false)).Contains(batch.RootId))
                throw new InvalidOperationException("Another namespace owner prevents the original SDK tree invocation.");
            var replay = await ReadRemoteRetirementTreeStartCoreAsync(operationId, batch, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null) { transaction.Commit(); return replay; }
            await ValidateRemoteRetirementCurrentAsync(TreeRootIntent(operationId, remote, batch.PreparedAt), transaction, cancellationToken).ConfigureAwait(false);
            int after = -1;
            while (true)
            {
                var page = await ReadRemoteRetirementTreePageCoreAsync(batch, after, 64, transaction, cancellationToken).ConfigureAwait(false);
                foreach (var entry in page.Entries)
                    if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(entry.Original.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false) != entry.Original)
                        throw new InvalidOperationException("The original SDK tree lost a current member, identity, parent, position or protection before invocation.");
                if (!page.HasMore) break;
                after = page.LastOrdinal;
            }
            var start = new MirrorPulseNamespaceRemoteRetirementTreeStart(1, operationId, remote.BatchFingerprint,
                batch.RemoteTreeFingerprint!, startedAt);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
            byte[] fingerprint = SHA256.HashData(payload);
            byte[] header = JsonSerializer.SerializeToUtf8Bytes(batch with { RemoteTreeStartFingerprint = Convert.ToHexString(fingerprint) }, TopologyJsonOptions);
            if (header.Length > 4 * 1024 * 1024 + 128) throw new InvalidOperationException("The original SDK tree start exceeds its retained header bound.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_remote_retirement_tree_starts(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", fingerprint); await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash WHERE operation_id=$id AND phase=0;";
            update.Parameters.AddWithValue("$id", operationId.ToString("D")); update.Parameters.AddWithValue("$payload", header);
            update.Parameters.AddWithValue("$hash", SHA256.HashData(header));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The original SDK invocation start lost its owning admission.");
            await CreateRemoteTreeSettlementOwnerAsync(batch, Convert.ToHexString(fingerprint), transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return start;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementTreeStart?> ReadNamespaceRemoteRetirementTreeStartAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original SDK tree operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var start = await ReadRemoteRetirementTreeStartCoreAsync(operationId, batch, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return start;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeStart?> ReadRemoteRetirementTreeStartCoreAsync(Guid operationId,
        MirrorPulseNamespaceRetirementBatch? batch, SqliteTransaction? transaction, CancellationToken token, bool requireSettlementOwner = true)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_remote_retirement_tree_starts WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (batch?.RemoteTreeStartFingerprint is not null) throw new InvalidDataException("The original SDK tree lost its durable invocation start.");
            await reader.DisposeAsync().ConfigureAwait(false);
            await RequireNoOrphanedRemoteTreeSettlementAsync(operationId, transaction, token).ConfigureAwait(false);
            return null;
        }
        byte[] payload = (byte[])reader[0], fingerprint = (byte[])reader[1];
        if (payload.Length > 131_072 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint) ||
            batch?.RemoteTreeStartFingerprint != Convert.ToHexString(fingerprint))
            throw new InvalidDataException("The original SDK tree start fingerprint is invalid.");
        MirrorPulseNamespaceRemoteRetirementTreeStart start;
        try
        {
            start = JsonSerializer.Deserialize<MirrorPulseNamespaceRemoteRetirementTreeStart>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The original SDK tree start is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The original SDK tree start payload is invalid.", error); }
        if (batch is null || batch.Version != 2 || batch.RemoteEntry is not { } remote || start.Version != 1 || start.OperationId != operationId ||
            start.BatchFingerprint != remote.BatchFingerprint || start.TreeFingerprint != batch.RemoteTreeFingerprint ||
            start.StartedAt == default || start.StartedAt < batch.PreparedAt ||
            !JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The original SDK tree start changed its immutable owner or chronology.");
        await reader.DisposeAsync().ConfigureAwait(false);
        if (requireSettlementOwner) _ = await ReadRemoteTreeSettlementProgressCoreAsync(batch, start, transaction, token).ConfigureAwait(false);
        return start;
    }

    private static async Task EnsureNamespaceRemoteRetirementTreeStartsSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_tree_starts(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_owners(operation_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
