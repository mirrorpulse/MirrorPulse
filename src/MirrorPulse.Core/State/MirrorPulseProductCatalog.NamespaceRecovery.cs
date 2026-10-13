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

    private async Task<IReadOnlyList<RootId>> ReadNamespaceRootFencesAsync(bool includeUnregisteredBirths,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            HashSet<RootId> roots = (await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
                .Where(intent => intent.IsPending).Select(intent => intent.RootId).ToHashSet();
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
                _ = await ReadPendingNamespaceMemberMoveCoreAsync(root, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending move lost its durable root fence.");
                roots.Add(root);
            }
            foreach (var retirement in await ReadPendingNamespaceRetirementsCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                roots.Add(retirement.Intent.RootId);
            foreach (var batch in await ReadPendingNamespaceRetirementBatchesCoreAsync(transaction, cancellationToken).ConfigureAwait(false))
                roots.Add(batch.RootId);
            await AddNamespaceReplacementRootFencesAsync(roots, transaction, cancellationToken).ConfigureAwait(false);
            if (includeUnregisteredBirths)
                await AddUnregisteredBirthRootFencesAsync(roots, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return Array.AsReadOnly(roots.OrderBy(root => root.Value).ToArray());
        }
        finally { _gate.Release(); }
    }

    private async Task AddUnregisteredBirthRootFencesAsync(HashSet<RootId> roots,
        SqliteTransaction transaction, CancellationToken token)
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
            roots.Add(birth.RootId);
        }
    }
}
