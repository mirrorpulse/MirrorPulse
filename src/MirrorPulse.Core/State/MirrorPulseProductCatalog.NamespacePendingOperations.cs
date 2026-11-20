using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespacePendingOperationKind { Birth, Move, Retirement, RetirementBatch, Replacement, BornMemberRecovery, RemoteUpdate, Transfer, RootLabelJournal, RootLabel, ConflictKeepRemote, RemoteDeleteRejection, JournalDirectoryMove }

/// <summary>A safe reference to retained local recovery work; it carries no native authority.</summary>
public sealed record MirrorPulseNamespacePendingOperation(Guid OperationId, RootId RootId, MirrorPulseNamespacePendingOperationKind Kind);

public sealed partial class MirrorPulseProductCatalog
{
    private sealed class NamespacePendingSelection(RootId rootId)
    {
        private int _birthCount;
        private bool _hasRootLabelJournal;

        public RootId RootId { get; } = rootId;
        public List<MirrorPulseNamespacePendingOperation> Operations { get; } = [];
        public MirrorPulseRootRenameIntent? RootLabel { get; set; }

        public void Add(RootId root, Guid operation, MirrorPulseNamespacePendingOperationKind kind)
        {
            if (kind == MirrorPulseNamespacePendingOperationKind.Birth && ++_birthCount > 4096)
                throw new InvalidDataException("Pending birth discovery exceeds its recovery bound.");
            if (root != RootId) return;
            if (kind == MirrorPulseNamespacePendingOperationKind.RootLabelJournal)
            {
                if (_hasRootLabelJournal)
                    throw new InvalidDataException("One root acquired multiple unfinished original ACK owners.");
                _hasRootLabelJournal = true;
            }
            Operations.Add(new(operation, root, kind));
        }
    }

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
            // Select work from validated owners in the same transaction. All roots must
            // pass the complete fence scan before the caller can receive any work.
            var pending = new NamespacePendingSelection(rootId);
            _ = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken, pending: pending).ConfigureAwait(false);
            await AddPendingUnsealedRootLabelOperationAsync(pending, transaction, cancellationToken).ConfigureAwait(false);
            if (pending.Operations.Count > 4096) throw new InvalidDataException("The root's pending local work exceeds its recovery bound.");
            transaction.Commit(); return pending.Operations.OrderBy(entry => entry.OperationId).ThenBy(entry => entry.Kind).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task AddPendingUnsealedRootLabelOperationAsync(NamespacePendingSelection pending,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (pending.RootLabel is not { } original || pending.Operations.Any(entry =>
            entry.Kind == MirrorPulseNamespacePendingOperationKind.Move && entry.OperationId == original.OperationId)) return;
        var history = await ReadManagedRootRenameHistoryCoreAsync(original.OperationId, token, transaction).ConfigureAwait(false);
        if (history.Intent != original)
            throw new InvalidDataException("Pending root Label discovery cannot replace its current original owner.");
        pending.Add(original.RootId, original.OperationId, MirrorPulseNamespacePendingOperationKind.RootLabel);
    }
}
