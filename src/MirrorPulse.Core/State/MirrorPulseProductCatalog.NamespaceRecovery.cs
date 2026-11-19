using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads one durable snapshot of roots awaiting product namespace recovery.</summary>
    /// <remarks>
    /// Includes managed-root Label changes and unresolved controlled member moves. Reading this
    /// snapshot neither settles an intent nor replaces the Host's shared admission and work drain.
    /// Damaged move indexes or evidence fail closed instead of exposing a partly moved namespace.
    /// </remarks>
    public Task<IReadOnlyList<RootId>> ReadNamespaceRecoveryRootsAsync(CancellationToken cancellationToken = default)
        => ReadNamespaceRootFencesAsync(includeUnregisteredBirths: false, cancellationToken);

    /// <summary>Reads roots whose namespace must be settled before ordinary synchronization dispatch.</summary>
    /// <remarks>
    /// Adds unregistered births to the recovery snapshot in the same transaction. A prepared,
    /// converted or observed birth is not dispatchable membership. Registration atomically
    /// releases this fence with its name reservation; recovery and bookkeeping remain available.
    /// </remarks>
    public Task<IReadOnlyList<RootId>> ReadNamespaceDispatchFencedRootsAsync(CancellationToken cancellationToken = default)
        => ReadNamespaceRootFencesAsync(includeUnregisteredBirths: true, cancellationToken);

    /// <summary>Checks whether the only durable fence in this root is this exact original remote move.</summary>
    /// <remarks>This grants no native dispatch or live root admission. The Host must recover the
    /// original SDK outcome and retain its shared work lease and namespace actor.</remarks>
    public async Task<bool> CanResumeNamespaceRemoteMoveAsync(MirrorPulseNamespaceMemberMoveIntent original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.RemoteEntry is null) throw new ArgumentException("Recovery requires an original remote move.", nameof(original));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var pending = await ReadPendingNamespaceMemberMoveCoreAsync(original.RootId, transaction, cancellationToken).ConfigureAwait(false);
            if (pending != original) { transaction.Commit(); return false; }
            var fenced = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken, original.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !fenced.Contains(original.RootId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks that this exact unregistered remote birth is the only durable fence in its root.</summary>
    /// <remarks>Only the original SDK entry may use this result. It grants no native or new-role authority.</remarks>
    public async Task<bool> CanResumeNamespaceRemoteBirthAsync(MirrorPulseNamespaceBirthIntent original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.RemoteEntry is null) throw new ArgumentException("Recovery requires an original remote birth.", nameof(original));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadNamespaceBirthCoreAsync(original.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained != original || await ReadNamespaceMemberForBirthCoreAsync(original.OperationId, transaction, cancellationToken).ConfigureAwait(false) is not null)
            { transaction.Commit(); return false; }
            var fenced = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteBirthId: original.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !fenced.Contains(original.RootId);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<RootId>> ReadNamespaceRootFencesAsync(bool includeUnregisteredBirths,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = BeginNamespaceRecoveryReadSnapshot();
            var roots = await ReadNamespaceRootFencesCoreAsync(includeUnregisteredBirths, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return OrderNamespaceRootFences(roots);
        }
        finally { _gate.Release(); }
    }

    private static ReadOnlyCollection<RootId> OrderNamespaceRootFences(HashSet<RootId> roots)
        => Array.AsReadOnly(roots.OrderBy(root => root.Value).ToArray());

    private async Task<HashSet<RootId>> ReadNamespaceRootFencesCoreAsync(bool includeUnregisteredBirths,
        SqliteTransaction transaction, CancellationToken cancellationToken, Guid? admittedRemoteMoveId = null, Guid? admittedRemoteBirthId = null,
        Guid? admittedRemoteRetirementId = null, Guid? admittedRemoteRetirementTreeId = null, Guid? admittedRemoteResolutionId = null,
        Guid? admittedRemoteUpdateId = null, Guid? admittedTransferId = null, Guid? admittedTransferBirthId = null,
        Guid? admittedRemoteDeleteRejectionId = null, NamespacePendingSelection? pending = null)
    {
        await RequireRemoteTreeSettlementOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
        await RequireRemoteDeleteResolutionOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
        HashSet<RootId> roots = [];
        void AddOperationFence(RootId root, Guid id, MirrorPulseNamespacePendingOperationKind kind)
        {
            roots.Add(root);
            pending?.Add(root, id, kind);
        }

        void AddTransferFences(Guid id, RootId source, RootId target)
        {
            AddOperationFence(source, id, MirrorPulseNamespacePendingOperationKind.Transfer);
            if (target != source) AddOperationFence(target, id, MirrorPulseNamespacePendingOperationKind.Transfer);
        }

        foreach (var move in await ReadPendingJournalDirectoryMovesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            AddOperationFence(move.RootId, move.OperationId, move.Kind);
        await foreach (var rejection in ReadPendingRemoteDeleteRejectionsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (rejection.Admission.OperationId != admittedRemoteDeleteRejectionId)
                AddOperationFence(rejection.Admission.RootId, rejection.Admission.OperationId, MirrorPulseNamespacePendingOperationKind.RemoteDeleteRejection);
        await AddKeepRemoteFileStartRootFencesAsync(roots, transaction, pending, cancellationToken).ConfigureAwait(false);
        await AddRootLabelJournalFencesAsync(roots, transaction, pending, cancellationToken).ConfigureAwait(false);
        await foreach (var update in ReadPendingRemoteUpdatesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (update.Plan.OperationId != admittedRemoteUpdateId)
                AddOperationFence(update.Plan.Child.Context.Member.RootId!.Value, update.Plan.OperationId, MirrorPulseNamespacePendingOperationKind.RemoteUpdate);
        await foreach (var rename in ReadNamespaceOwnerRowsCoreAsync(NamespaceRootRenamePageSql, transaction,
            static (row, rootId) => ReadRootRenameIntent(row.GetString(1), rootId.ToString("D")), cancellationToken).ConfigureAwait(false))
        {
            if (rename.IsPending)
            {
                roots.Add(rename.RootId);
                if (pending is not null && pending.RootId == rename.RootId) pending.RootLabel = rename;
            }
            else if (rename.Phase == MirrorPulseRootRenamePhase.Cancelled)
            {
                // A sealed cancellation must retain both original owners. Missing
                // pending rows alone never turn an unfinished move into readiness.
                var move = await ReadNamespaceMemberMoveCoreAsync(rename.OperationId, transaction, cancellationToken).ConfigureAwait(false);
                if (rename.CancellationOperationId is not null && (move is null ||
                    await ReadRootLabelCancellationRowAsync(rename.OperationId, transaction, cancellationToken).ConfigureAwait(false) is null))
                    throw new InvalidDataException("The cancelled protected Label lost its original guarded terminal ownership.");
                if (rename.UnsealedCancellationOperationId is not null && (move is not null ||
                    await ReadUnsealedRootLabelCancellationCoreAsync(rename.OperationId, transaction, cancellationToken).ConfigureAwait(false) is null))
                    throw new InvalidDataException("The cancelled unsealed Label lost its original guarded terminal ownership.");
            }
        }
        await foreach (var transfer in ReadPendingNamespaceTransfersCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (transfer.Plan.OperationId != admittedTransferId)
                AddTransferFences(transfer.Plan.OperationId, transfer.Plan.SourceRoute.RootId, transfer.Plan.TargetRoute.RootId);
        foreach (var transfer in await ReadDirectoryTransferFencesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            AddTransferFences(transfer.OperationId, transfer.Source, transfer.Target);
        await foreach (Guid id in ReadNamespaceOwnerIdsCoreAsync(NamespacePendingMoveRootPageSql, transaction, cancellationToken).ConfigureAwait(false))
        {
            var root = new RootId(id);
            var move = await ReadPendingNamespaceMemberMoveCoreAsync(root, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending move lost its durable root fence.");
            if (move.OperationId != admittedRemoteMoveId || move.RemoteEntry is null)
                AddOperationFence(root, move.OperationId, MirrorPulseNamespacePendingOperationKind.Move);
        }
        await foreach (var retirement in ReadPendingNamespaceRetirementsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            var intent = retirement.Intent;
            if (retirement.OperationId == admittedRemoteRetirementId && intent.RemoteEntry is not null ||
                admittedTransferId is not null && intent.TransferEntry?.OperationId == admittedTransferId ||
                admittedRemoteResolutionId is not null && intent.BatchOperationId == admittedRemoteResolutionId &&
                    await IsRemoteDeleteResolutionBatchAsync(admittedRemoteResolutionId.Value, transaction, cancellationToken).ConfigureAwait(false))
                continue;

            roots.Add(intent.RootId);
            if (pending is null || pending.RootId != intent.RootId) continue;
            if (intent.BatchOperationId is not null)
            {
                // The sealed owner publishes the recovery operation. Validate this leaf's membership here.
                await RequireNamespaceRetirementBatchEntryAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            }
            else pending.Add(intent.RootId, intent.OperationId, MirrorPulseNamespacePendingOperationKind.Retirement);
        }
        await foreach (var batch in ReadPendingNamespaceRetirementBatchesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (!(batch.OperationId == admittedRemoteRetirementTreeId && batch.RemoteEntry is not null) &&
                !(batch.OperationId == admittedRemoteResolutionId && batch.RemoteConflictCommandId == admittedRemoteResolutionId))
                AddOperationFence(batch.RootId, batch.OperationId, MirrorPulseNamespacePendingOperationKind.RetirementBatch);
        await AddNamespaceReplacementRootFencesAsync(roots, transaction, pending, cancellationToken).ConfigureAwait(false);
        await AddNamespaceInitializationRootFencesAsync(roots, transaction, cancellationToken).ConfigureAwait(false);
        await foreach (var action in ReadPendingBornMemberRecoveryActionsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            AddOperationFence(action.Child.Member.RootId!.Value, action.ActionId, MirrorPulseNamespacePendingOperationKind.BornMemberRecovery);
        if (includeUnregisteredBirths)
        {
            await AddUnregisteredBirthRootFencesAsync(roots, transaction, cancellationToken, admittedRemoteBirthId,
                admittedTransferId, admittedTransferBirthId, pending).ConfigureAwait(false);
        }
        return roots;
    }

    private async Task AddUnregisteredBirthRootFencesAsync(HashSet<RootId> roots,
        SqliteTransaction transaction, CancellationToken token, Guid? admittedRemoteBirthId = null,
        Guid? admittedTransferId = null, Guid? admittedTransferBirthId = null,
        NamespacePendingSelection? pending = null)
    {
        // Either side can retain evidence of a damaged or incomplete birth.
        await foreach (Guid operation in ReadNamespaceOwnerIdsCoreAsync(NamespaceUnregisteredBirthPageSql, transaction, token).ConfigureAwait(false))
        {
            var birth = await ReadNamespaceBirthCoreAsync(operation, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending birth reservation lost its original admission.");
            if (await ReadNamespaceMemberForBirthCoreAsync(operation, transaction, token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("A registered birth retains an unresolved name reservation.");
            try
            {
                await RequireNamespaceBirthReservationAsync(birth, transaction, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException("The pending birth lost its original name reservation.", exception);
            }
            bool ownRemote = operation == admittedRemoteBirthId && birth.RemoteEntry is not null;
            bool ownTransfer = operation == admittedTransferBirthId && admittedTransferId is not null &&
                birth.TransferEntry?.OperationId == admittedTransferId;
            if (!ownRemote && !ownTransfer)
            {
                roots.Add(birth.RootId);
                pending?.Add(birth.RootId, operation, MirrorPulseNamespacePendingOperationKind.Birth);
            }
        }
    }
}
