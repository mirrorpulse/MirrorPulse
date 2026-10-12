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
    public async Task<IReadOnlyList<RootId>> ReadNamespaceRecoveryRootsAsync(CancellationToken cancellationToken = default)
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
            transaction.Commit();
            return Array.AsReadOnly(roots.OrderBy(root => root.Value).ToArray());
        }
        finally { _gate.Release(); }
    }
}
