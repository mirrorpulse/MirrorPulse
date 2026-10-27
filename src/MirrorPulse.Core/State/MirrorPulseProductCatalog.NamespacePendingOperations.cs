using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespacePendingOperationKind { Birth, Move, Retirement, RetirementBatch, Replacement, BornMemberRecovery, RemoteUpdate, Transfer, RootLabelJournal, RootLabel }

/// <summary>A safe reference to retained local recovery work; it carries no native authority.</summary>
public sealed record MirrorPulseNamespacePendingOperation(Guid OperationId, RootId RootId, MirrorPulseNamespacePendingOperationKind Kind);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Discovers validated pending local work before startup reserves another initializer.</summary>
    /// <remarks>Initialization has its own route-preserving discovery. This query changes no
    /// completion, reservation, current protection or official journal record.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespacePendingOperation>> ReadNamespacePendingOperationsAsync(RootId rootId,
        CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root ID is required.", nameof(rootId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            // Validate the complete reservation indexes first. Missing or corrupt work must
            // not turn a query for another root into a false declaration of readiness.
            _ = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken).ConfigureAwait(false);
            var result = new List<MirrorPulseNamespacePendingOperation>();
            if (await ReadPendingRootLabelJournalForRootCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } rootJournal)
                result.Add(new(rootJournal.Observation.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RootLabelJournal));
            await foreach (var transfer in ReadPendingNamespaceTransfersCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (transfer.Plan.SourceRoute.RootId == rootId || transfer.Plan.TargetRoute.RootId == rootId)
                    result.Add(new(transfer.Plan.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Transfer));
            await foreach (var update in ReadPendingRemoteUpdatesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (update.Plan.Child.Context.Member.RootId == rootId)
                    result.Add(new(update.Plan.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RemoteUpdate));
            if (await ReadPendingNamespaceMemberMoveCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } move)
                result.Add(new(move.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Move));
            await AddPendingUnsealedRootLabelOperationAsync(result, rootId, transaction, cancellationToken).ConfigureAwait(false);
            await foreach (var retirement in ReadPendingNamespaceRetirementsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (retirement.Intent.RootId == rootId)
                    result.Add(new(retirement.Intent.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Retirement));
            await foreach (var batch in ReadPendingNamespaceRetirementBatchesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (batch.RootId == rootId)
                    result.Add(new(batch.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RetirementBatch));
            if (await ReadPendingNamespaceReplacementCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } replacement)
                result.Add(new(replacement.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Replacement));
            await foreach (var born in ReadPendingBornMemberRecoveryActionsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (born.Child.Member.RootId == rootId)
                    result.Add(new(born.ActionId, rootId, MirrorPulseNamespacePendingOperationKind.BornMemberRecovery));
            var births = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT b.operation_id FROM namespace_birth_intents b
                    LEFT JOIN namespace_members m ON m.birth_operation_id=b.operation_id
                    WHERE m.member_id IS NULL ORDER BY b.operation_id LIMIT 4097;
                    """;
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    births.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            if (births.Count > 4096) throw new InvalidDataException("Pending birth discovery exceeds its recovery bound.");
            foreach (Guid operation in births)
            {
                var birth = await ReadNamespaceBirthCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending birth lost its validated original admission.");
                if (birth.RootId == rootId) result.Add(new(operation, rootId, MirrorPulseNamespacePendingOperationKind.Birth));
            }
            if (result.Count > 4096) throw new InvalidDataException("The root's pending local work exceeds its recovery bound.");
            transaction.Commit(); return result.OrderBy(entry => entry.OperationId).ThenBy(entry => entry.Kind).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task AddPendingUnsealedRootLabelOperationAsync(List<MirrorPulseNamespacePendingOperation> result, RootId rootId,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseRootRenameIntent? current;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,root_id FROM managed_root_renames WHERE root_id=$root;";
            query.Parameters.AddWithValue("$root", rootId.ToString());
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            current = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRootRenameIntent(reader.GetString(0), reader.GetString(1)) : null;
        }
        if (current is not { IsPending: true } original || result.Any(entry =>
            entry.Kind == MirrorPulseNamespacePendingOperationKind.Move && entry.OperationId == original.OperationId)) return;
        var history = await ReadManagedRootRenameHistoryCoreAsync(original.OperationId, token, transaction).ConfigureAwait(false);
        if (history.Intent != original)
            throw new InvalidDataException("Pending root Label discovery cannot replace its current original owner.");
        result.Add(new(original.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RootLabel));
    }
}
