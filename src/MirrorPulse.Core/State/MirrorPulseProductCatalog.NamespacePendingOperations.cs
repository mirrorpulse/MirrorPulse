using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespacePendingOperationKind { Birth, Move, Retirement, RetirementBatch, Replacement, BornMemberRecovery, RemoteUpdate, Transfer, RootLabelJournal, RootLabel, ConflictKeepRemote, RemoteDeleteRejection, JournalDirectoryMove }

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
            var result = new List<MirrorPulseNamespacePendingOperation>();
            int pendingBirths = 0;
            void SelectPending(RootId root, Guid operation, MirrorPulseNamespacePendingOperationKind kind)
            {
                if (kind == MirrorPulseNamespacePendingOperationKind.Birth && ++pendingBirths > 4096)
                    throw new InvalidDataException("Pending birth discovery exceeds its recovery bound.");
                if (root == rootId) result.Add(new(operation, root, kind));
            }

            // Select work from validated owners in the same transaction. All roots must
            // pass the complete fence scan before the caller can receive any work.
            _ = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken, selectPending: SelectPending).ConfigureAwait(false);
            if (await ReadPendingRootLabelJournalForRootCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } rootJournal)
                result.Add(new(rootJournal.Observation.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RootLabelJournal));
            if (await ReadPendingKeepRemoteFileStartForRootCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } keepRemote)
                result.Add(new(keepRemote.CommandId, rootId, MirrorPulseNamespacePendingOperationKind.ConflictKeepRemote));
            if (await ReadPendingNamespaceMemberMoveCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } move)
                result.Add(new(move.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Move));
            await AddPendingUnsealedRootLabelOperationAsync(result, rootId, transaction, cancellationToken).ConfigureAwait(false);
            await foreach (var retirement in ReadPendingNamespaceRetirementsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            {
                if (retirement.Intent.RootId != rootId) continue;
                if (retirement.Intent.BatchOperationId is not null)
                {
                    // Recovery routes to the original sealed owner; the leaf remains individually queryable.
                    await RequireNamespaceRetirementBatchEntryAsync(retirement.Intent, transaction, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                result.Add(new(retirement.Intent.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Retirement));
            }
            await foreach (var batch in ReadPendingNamespaceRetirementBatchesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                if (batch.RootId == rootId)
                    result.Add(new(batch.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.RetirementBatch));
            if (await ReadPendingNamespaceReplacementCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false) is { } replacement)
                result.Add(new(replacement.OperationId, rootId, MirrorPulseNamespacePendingOperationKind.Replacement));
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
