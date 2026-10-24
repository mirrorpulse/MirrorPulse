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
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var roots = await ReadNamespaceRootFencesCoreAsync(includeUnregisteredBirths, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return roots;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<RootId>> ReadNamespaceRootFencesCoreAsync(bool includeUnregisteredBirths,
        SqliteTransaction transaction, CancellationToken cancellationToken, Guid? admittedRemoteMoveId = null, Guid? admittedRemoteBirthId = null,
        Guid? admittedRemoteRetirementId = null, Guid? admittedRemoteRetirementTreeId = null, Guid? admittedRemoteResolutionId = null,
        Guid? admittedRemoteUpdateId = null, Guid? admittedTransferId = null, Guid? admittedTransferBirthId = null)
    {
        await RequireRemoteTreeSettlementOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
        await RequireRemoteDeleteResolutionOwnershipIntegrityAsync(transaction, cancellationToken).ConfigureAwait(false);
        var updates = await ReadPendingRemoteUpdatesCoreAsync(transaction, cancellationToken).ConfigureAwait(false);
        HashSet<RootId> roots = (await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
            .Where(intent => intent.IsPending).Select(intent => intent.RootId).ToHashSet();
        foreach (var update in updates)
            if (update.Plan.OperationId != admittedRemoteUpdateId) roots.Add(update.Plan.Child.Context.Member.RootId!.Value);
        await foreach (var transfer in ReadPendingNamespaceTransfersCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (transfer.Plan.OperationId != admittedTransferId)
            {
                roots.Add(transfer.Plan.SourceRoute.RootId);
                roots.Add(transfer.Plan.TargetRoute.RootId);
            }
        var moveRoots = new List<RootId>();
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT root_id FROM namespace_member_move_pending ORDER BY root_id;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string stored = reader.GetString(0);
                if (!RootId.TryParse(stored, out RootId root) || root.ToString() != stored)
                    throw new InvalidDataException("The pending move root index is invalid.");
                moveRoots.Add(root);
            }
        }
        foreach (RootId root in moveRoots)
        {
            var move = await ReadPendingNamespaceMemberMoveCoreAsync(root, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending move lost its durable root fence.");
            if (move.OperationId != admittedRemoteMoveId || move.RemoteEntry is null) roots.Add(root);
        }
        foreach (var retirement in await ReadPendingNamespaceRetirementsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (!(retirement.OperationId == admittedRemoteRetirementId && retirement.Intent.RemoteEntry is not null) &&
                !(admittedTransferId is not null && retirement.Intent.TransferEntry?.OperationId == admittedTransferId) &&
                !(retirement.Intent.BatchOperationId == admittedRemoteResolutionId && admittedRemoteResolutionId is not null &&
                    await IsRemoteDeleteResolutionBatchAsync(admittedRemoteResolutionId.Value, transaction, cancellationToken).ConfigureAwait(false))) roots.Add(retirement.Intent.RootId);
        foreach (var batch in await ReadPendingNamespaceRetirementBatchesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            if (!(batch.OperationId == admittedRemoteRetirementTreeId && batch.RemoteEntry is not null) &&
                !(batch.OperationId == admittedRemoteResolutionId && batch.RemoteConflictCommandId == admittedRemoteResolutionId)) roots.Add(batch.RootId);
        await AddNamespaceReplacementRootFencesAsync(roots, transaction, cancellationToken).ConfigureAwait(false);
        await AddNamespaceInitializationRootFencesAsync(roots, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var action in await ReadPendingBornMemberRecoveryActionsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
            roots.Add(action.Child.Member.RootId!.Value);
        if (includeUnregisteredBirths)
        {
            await AddUnregisteredBirthRootFencesAsync(roots, transaction, cancellationToken, admittedRemoteBirthId,
                admittedTransferId, admittedTransferBirthId).ConfigureAwait(false);
        }
        return Array.AsReadOnly(roots.OrderBy(root => root.Value).ToArray());
    }

    private async Task AddUnregisteredBirthRootFencesAsync(HashSet<RootId> roots,
        SqliteTransaction transaction, CancellationToken token, Guid? admittedRemoteBirthId = null,
        Guid? admittedTransferId = null, Guid? admittedTransferBirthId = null)
    {
        var operations = new List<Guid>();
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            // The intent side keeps a missing reservation from silently releasing a fence.
            // The reservation side detects orphaned or erroneously retained reservations.
            query.CommandText = """
                SELECT b.operation_id FROM namespace_birth_intents b
                LEFT JOIN namespace_members m ON m.birth_operation_id=b.operation_id
                WHERE m.member_id IS NULL
                UNION SELECT operation_id FROM namespace_birth_reservations
                ORDER BY operation_id;
                """;
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string stored = reader.GetString(0);
                if (!Guid.TryParseExact(stored, "D", out Guid operation) || operation == Guid.Empty ||
                    stored != operation.ToString("D"))
                    throw new InvalidDataException("The pending birth operation index is invalid.");
                operations.Add(operation);
            }
        }
        foreach (Guid operation in operations)
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
            if (!ownRemote && !ownTransfer) roots.Add(birth.RootId);
        }
    }
}
