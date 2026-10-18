using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Historical original SDK tree recovery facts, not a fresh native observation or mutation authority.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeStatus(Guid OperationId, Guid MemberId, RootId RootId,
    InstanceId InstanceId, string BatchId, int Ordinal, string RelativePath, string Phase, string Stage,
    DateTimeOffset PreparedAt, DateTimeOffset? StartedAt, string? RootOutcome, bool? RootNativeDeleted,
    bool? RootOfficialTombstone, string? RootRemoteRevision, Guid? ConflictId, string? ConflictReason,
    int MemberCount, int SettledMembers, int RemovedMembers, int RetainedMembers, int? NextOrdinal,
    DateTimeOffset? OutcomeObservedAt, DateTimeOffset? CompletedAt, string PackageVersion, string PackageSource);

public sealed record MirrorPulseNamespaceRemoteRetirementTreeStatusPage(
    IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeStatus> Entries, Guid? NextOperationId);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads one coherent original tree status without inspecting native state or dispatching work.</summary>
    public async Task<MirrorPulseNamespaceRemoteRetirementTreeStatus?> ReadNamespaceRemoteRetirementTreeStatusAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original directory operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await RequireRemoteTreeSettlementOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
            var result = await ReadRemoteTreeStatusCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lists at most 64 original SDK trees for one stable root with an indexed continuation.</summary>
    public async Task<MirrorPulseNamespaceRemoteRetirementTreeStatusPage> ReadNamespaceRemoteRetirementTreeStatusesAsync(
        RootId rootId, Guid? afterOperationId = null, int limit = 64, CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty || afterOperationId == Guid.Empty || limit is < 1 or > 64)
            throw new ArgumentException("Original directory status requires a root, a valid continuation and a page of at most 64.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await RequireRemoteTreeSettlementOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
            var operations = new List<Guid>(limit + 1);
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT b.operation_id FROM namespace_member_retirement_batches b
                    JOIN namespace_remote_retirement_tree_owners o ON o.operation_id=b.operation_id
                    WHERE b.root_id=$root AND b.operation_id>$after
                    ORDER BY b.operation_id LIMIT $limit;
                    """;
                query.Parameters.AddWithValue("$root", rootId.ToString()); query.Parameters.AddWithValue("$after", afterOperationId?.ToString("D") ?? string.Empty);
                query.Parameters.AddWithValue("$limit", limit + 1);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            var result = new List<MirrorPulseNamespaceRemoteRetirementTreeStatus>(Math.Min(limit, operations.Count));
            foreach (Guid operation in operations.Take(limit))
            {
                var entry = await ReadRemoteTreeStatusCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original directory status index lost its owning admission.");
                if (entry.RootId != rootId) throw new InvalidDataException("The original directory status page changed its stable root.");
                result.Add(entry);
            }
            transaction.Commit(); return new(result.AsReadOnly(), operations.Count > limit ? result[^1].OperationId : null);
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementTreeStatus?> ReadRemoteTreeStatusCoreAsync(Guid operation,
        SqliteTransaction transaction, CancellationToken token)
    {
        var batch = await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token).ConfigureAwait(false);
        if (batch is null)
        {
            await RequireNoOrphanedRemoteTreeSettlementAsync(operation, transaction, token).ConfigureAwait(false); return null;
        }
        if (batch.RemoteEntry is not { } remote) throw new InvalidOperationException("Controlled retirement has no original SDK directory status.");
        var start = await ReadRemoteRetirementTreeStartCoreAsync(operation, batch, transaction, token).ConfigureAwait(false);
        var progress = start is null ? null : await ReadRemoteTreeSettlementProgressCoreAsync(batch, start, transaction, token).ConfigureAwait(false);
        var outcome = await ReadRemoteTreeOutcomeCoreAsync(operation, transaction, token).ConfigureAwait(false);
        int settled = progress is null ? 0 : batch.Entries.Count - progress.NextOrdinal - 1;
        await using var count = _connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*),COALESCE(SUM(native_missing),0) FROM namespace_remote_retirement_tree_member_settlements WHERE operation_id=$id;";
        count.Parameters.AddWithValue("$id", operation.ToString("D"));
        await using var reader = await count.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(0) != settled)
            throw new InvalidDataException("The original directory status lost its contiguous committed member count.");
        int removed = reader.GetInt32(1);
        string stage = batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.RecoveryRequired ? "RecoveryRequired" :
            progress?.CompletedAt is not null ? "Completed" : start is null ? "Admitted" : outcome is null ? "InvocationMayHaveBegun" :
            progress!.NextOrdinal < 0 ? "AwaitingCompletion" : "SettlingMembers";
        return new(operation, batch.MemberId, batch.RootId, remote.InstanceId, remote.BatchId, remote.Ordinal,
            remote.Child.Context.Location.RelativePath, batch.Phase.ToString(), stage, batch.PreparedAt, start?.StartedAt,
            outcome?.EntryStatus, outcome?.NativeMissing, outcome?.OfficialTombstone, outcome?.OfficialRemoteRevision,
            outcome?.ConflictId, outcome?.ConflictReason, batch.Entries.Count, settled, removed, settled - removed,
            progress?.NextOrdinal, outcome?.VerifiedAt, progress?.CompletedAt, remote.PackageVersion, remote.PackageSource);
    }

    private static async Task EnsureRemoteTreeStatusIndexAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS namespace_retirement_batches_root_operation ON namespace_member_retirement_batches(root_id,operation_id);";
        await index.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
