using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads at most two original remote owners; a rejected deletion leaves its item current.</summary>
    /// <remarks>A pending or legacy completed owner fences reuse until its own lifecycle is reconciled.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceRetirement>> ReadNamespaceRemoteRetirementGenerationsAsync(
        Guid itemId, CancellationToken token = default)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("A public logical item is required.", nameof(itemId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRemoteRetirementGenerationsCoreAsync(itemId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceRetirement>> ReadRemoteRetirementGenerationsCoreAsync(
        Guid itemId, SqliteTransaction transaction, CancellationToken token)
    {
        var ids = new List<Guid>(2);
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id FROM namespace_member_retirements INDEXED BY namespace_remote_retirement_items
                WHERE json_extract(CAST(payload AS TEXT),'$.Intent.RemoteEntry.ItemId')=$item
                    AND json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NOT NULL AND phase<>4
                ORDER BY operation_id LIMIT 2;
                """;
            query.Parameters.AddWithValue("$item", itemId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        var result = new List<MirrorPulseNamespaceRetirement>(ids.Count);
        foreach (Guid id in ids)
        {
            var retirement = await ReadNamespaceRetirementCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The indexed remote generation lost its original retirement.");
            if (retirement.Intent.RemoteEntry?.ItemId != itemId || retirement.Phase == MirrorPulseNamespaceRetirementPhase.RemoteRejected)
                throw new InvalidDataException("The original remote-item index disagrees with its retirement owner.");
            result.Add(retirement);
        }
        return result.AsReadOnly();
    }

    private async Task RequireOrdinaryRemoteIdentityRetirementAsync(MirrorPulseRemoteIdentityGeneration selection,
        SqliteTransaction transaction, CancellationToken token)
    {
        var owners = await ReadRemoteRetirementGenerationsCoreAsync(selection.PreviousItemId, transaction, token).ConfigureAwait(false);
        if (owners.Count != 1 || owners[0].OperationId != selection.RetirementOperationId)
            throw new InvalidOperationException("An ambiguous or reused remote item requires explicit generation reconciliation.");
        var retirement = owners[0]; var original = retirement.Intent.RemoteEntry!;
        if (original.Version != 2 || original.InstanceId != selection.InstanceId || original.RootKey != selection.RootKey ||
            retirement.Intent.RootId != selection.RootId || original.ItemId != selection.PreviousItemId ||
            original.RemoteId != selection.PreviousRemoteId || retirement.StartedItemId != selection.PreviousItemId ||
            retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.RemoteSourceNameEpoch != selection.RetiredNameEpoch ||
            retirement.CompletedAt is not { } completedAt || selection.SelectedAt < completedAt)
            throw new InvalidOperationException("A successor requires its original completed remote deletion and transactional name epoch.");
        // Reading the original validates the actual correlated SDK outcome, native absence and exact public tombstone.
        await using var transfer = _connection.CreateCommand(); transfer.Transaction = transaction;
        transfer.CommandText = """
            SELECT 1 FROM namespace_member_retirements INDEXED BY namespace_transfer_source_items
            WHERE json_extract(CAST(payload AS TEXT),'$.StartedItemId')=$item
                AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL LIMIT 1;
            """;
        transfer.Parameters.AddWithValue("$item", selection.PreviousItemId.ToString("D"));
        if (await transfer.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A remote generation cannot borrow a transfer's source retirement.");
    }
}
