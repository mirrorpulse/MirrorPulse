using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Admits only the same immutable SDK tree through its own fence.</summary>
    /// <remarks>This is catalog ownership, not proof of native deletion, an SDK result or a safe cursor.</remarks>
    public async Task<bool> CanResumeNamespaceRemoteRetirementTreeAsync(MirrorPulseNamespaceRetirementBatch original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Version != 2 || original.RemoteEntry is null)
            throw new ArgumentException("Recovery requires the original SDK tree admission.", nameof(original));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadNamespaceRetirementBatchCoreAsync(original.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained is null || retained.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared || original.Phase != retained.Phase ||
                retained.Version != original.Version || retained.MemberId != original.MemberId || retained.RootId != original.RootId ||
                retained.PreparedAt != original.PreparedAt || retained.RemoteEntry != original.RemoteEntry ||
                retained.RemoteTreeFingerprint != original.RemoteTreeFingerprint || !retained.Entries.SequenceEqual(original.Entries))
            { transaction.Commit(); return false; }
            var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteRetirementTreeId: original.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !fences.Contains(original.RootId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads at most 64 unresolved original tree owners for one unchanged SDK batch.</summary>
    public async Task<IReadOnlyList<MirrorPulseNamespaceRetirementBatch>> ReadPendingNamespaceRemoteRetirementTreesAsync(
        InstanceId instance, string batchId, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instance, batchId, 0);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var operations = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT e.operation_id FROM namespace_remote_retirement_tree_owners e
                    JOIN namespace_member_retirement_batches b ON b.operation_id=e.operation_id
                    WHERE e.instance_id=$instance AND e.batch_id=$batch AND b.phase<>$completed
                    ORDER BY e.ordinal LIMIT 65;
                    """;
                query.Parameters.AddWithValue("$instance", instance.ToString()); query.Parameters.AddWithValue("$batch", batchId);
                query.Parameters.AddWithValue("$completed", (int)MirrorPulseNamespaceRetirementBatchPhase.Completed);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            if (operations.Count > 64) throw new InvalidDataException("The original SDK batch has too many unresolved tree owners.");
            var result = new List<MirrorPulseNamespaceRetirementBatch>(operations.Count);
            foreach (var operation in operations)
            {
                var retained = await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending SDK tree lost its original admission.");
                if (retained.RemoteEntry is not { } remote || remote.InstanceId != instance || remote.BatchId != batchId)
                    throw new InvalidDataException("The pending SDK tree changed its original batch owner.");
                result.Add(retained);
            }
            transaction.Commit(); return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }
}
