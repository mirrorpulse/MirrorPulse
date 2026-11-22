using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string ControlledRetirementItemIndexSql = """
        CREATE INDEX namespace_controlled_retirement_items ON namespace_member_retirements(
            json_extract(CAST(payload AS TEXT),'$.StartedItemId'),operation_id)
        WHERE json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NULL
            AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NULL
            AND json_extract(CAST(payload AS TEXT),'$.StartedItemId') IS NOT NULL
        """;

    /// <summary>Reads at most two original controlled or explicit-decision invocations for one public item.</summary>
    /// <remarks>A legacy or generic controlled deletion remains a fence; this lookup supplies no missing lifecycle witness.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceRetirement>> ReadNamespaceControlledRetirementGenerationsAsync(
        Guid itemId, CancellationToken token = default)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("An original public item is required.", nameof(itemId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadControlledRetirementGenerationsCoreAsync(itemId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceRetirement>> ReadControlledRetirementGenerationsCoreAsync(
        Guid itemId, SqliteTransaction transaction, CancellationToken token)
    {
        var ids = new List<Guid>(2);
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT operation_id FROM namespace_member_retirements INDEXED BY namespace_controlled_retirement_items
                WHERE json_extract(CAST(payload AS TEXT),'$.StartedItemId')=$item
                    AND json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NULL
                    AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NULL
                    AND json_extract(CAST(payload AS TEXT),'$.StartedItemId') IS NOT NULL
                ORDER BY operation_id LIMIT 2;
                """;
            query.Parameters.AddWithValue("$item", itemId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        var result = new List<MirrorPulseNamespaceRetirement>(ids.Count);
        foreach (Guid id in ids)
        {
            var original = await ReadNamespaceRetirementCoreAsync(id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The indexed controlled invocation lost its original retirement.");
            if (original.StartedItemId != itemId || original.Intent.RemoteEntry is not null || original.Intent.TransferEntry is not null)
                throw new InvalidDataException("The controlled-item index disagrees with its original invocation.");
            result.Add(original);
        }
        return result.AsReadOnly();
    }

    private async Task RequireExplicitRemoteIdentityRetirementAsync(MirrorPulseRemoteIdentityGeneration selection,
        SqliteTransaction transaction, CancellationToken token)
    {
        var owners = await ReadControlledRetirementGenerationsCoreAsync(selection.PreviousItemId, transaction, token).ConfigureAwait(false);
        if (owners.Count != 1 || owners[0].OperationId != selection.RetirementOperationId || owners[0].Intent.BatchOperationId is not { } commandId)
            throw new InvalidOperationException("A successor requires its sole original explicit deletion invocation.");
        var retirement = owners[0];
        var decision = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
        var entry = decision.Plan.Entries.Single(entry => entry.Original.Context.Member.MemberId == retirement.Intent.MemberId);
        var command = await ReadRemoteConflictActionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
        if (decision.Plan.Version != 2 || decision.Plan.RootKey != selection.RootKey ||
            decision.Plan.Decision.Conflict.InstanceId != selection.InstanceId || retirement.Intent.RootId != selection.RootId ||
            entry.Original.Identity.ItemId != selection.PreviousItemId || entry.Original.Identity.RemoteId != selection.PreviousRemoteId ||
            retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.ExplicitSourceNameEpoch != selection.RetiredNameEpoch ||
            decision.CompletedAt is not { } completedAt || selection.SelectedAt < completedAt ||
            command?.Receipt is not { } receipt || receipt.Intent != decision.Plan.Decision ||
            receipt.Status is not (MirrorPulseRemoteConflictObservedStatus.Applied or MirrorPulseRemoteConflictObservedStatus.AlreadyApplied) ||
            receipt.ObservedAt < completedAt || selection.SelectedAt < receipt.ObservedAt)
            throw new InvalidOperationException("A successor requires its exact settled explicit decision, name epoch and original command receipt.");
        if ((await ReadRemoteRetirementGenerationsCoreAsync(selection.PreviousItemId, transaction, token).ConfigureAwait(false)).Count != 0)
            throw new InvalidOperationException("An explicit decision cannot borrow an automatic remote deletion.");
        await using var transfer = _connection.CreateCommand(); transfer.Transaction = transaction;
        transfer.CommandText = """
            SELECT 1 FROM namespace_member_retirements INDEXED BY namespace_transfer_source_items
            WHERE json_extract(CAST(payload AS TEXT),'$.StartedItemId')=$item
                AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL LIMIT 1;
            """;
        transfer.Parameters.AddWithValue("$item", selection.PreviousItemId.ToString("D"));
        if (await transfer.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("An explicit decision cannot borrow a transfer retirement.");
    }

    private static async Task EnsureControlledRetirementGenerationSchemaAsync(SqliteConnection connection,
        long originalVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='namespace_controlled_retirement_items';";
        var existing = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null)
        {
            if (originalVersion >= 89) throw new InvalidDataException("Schema89 lost its original controlled-retirement lookup.");
            schema.CommandText = ControlledRetirementItemIndexSql;
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql || NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(ControlledRetirementItemIndexSql))
            throw new InvalidDataException("The controlled-retirement index changed its original invocation address.");
        if (originalVersion < 89)
        {
            schema.CommandText = "PRAGMA user_version=89;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
